-- Column-level tracking for PostgreSQL (optional). Run 01-change-tracking-setup.postgres.sql first. The trigger records the names of changed columns: consumers can distinguish an unchanged column from a column set to NULL.

-- Add the change mask to the outbox created by 01
ALTER TABLE public.training_sessions_outbox
    ADD COLUMN IF NOT EXISTS changed TEXT[];

-- Capture trigger that records changed column names. IS DISTINCT FROM detects changes to and from NULL.
CREATE OR REPLACE FUNCTION public.training_sessions_capture() RETURNS TRIGGER AS $$
DECLARE
    changed_columns TEXT[] := '{}';
BEGIN
    IF TG_OP = 'DELETE' THEN
        INSERT INTO public.training_sessions_outbox (operation, session_id)
        VALUES ('D', OLD.id);
        RETURN OLD;
    END IF;

    IF TG_OP = 'UPDATE' THEN
        IF NEW.recorded_on       IS DISTINCT FROM OLD.recorded_on       THEN changed_columns := changed_columns || 'RecordedOn'::TEXT;       END IF;
        IF NEW.type              IS DISTINCT FROM OLD.type              THEN changed_columns := changed_columns || 'Type'::TEXT;              END IF;
        IF NEW.steps             IS DISTINCT FROM OLD.steps             THEN changed_columns := changed_columns || 'Steps'::TEXT;             END IF;
        IF NEW.distance          IS DISTINCT FROM OLD.distance          THEN changed_columns := changed_columns || 'Distance'::TEXT;          END IF;
        IF NEW.duration          IS DISTINCT FROM OLD.duration          THEN changed_columns := changed_columns || 'Duration'::TEXT;          END IF;
        IF NEW.calories          IS DISTINCT FROM OLD.calories          THEN changed_columns := changed_columns || 'Calories'::TEXT;          END IF;
        IF NEW.post_processed_on IS DISTINCT FROM OLD.post_processed_on THEN changed_columns := changed_columns || 'PostProcessedOn'::TEXT; END IF;
        IF NEW.adjusted_steps    IS DISTINCT FROM OLD.adjusted_steps    THEN changed_columns := changed_columns || 'AdjustedSteps'::TEXT;    END IF;
        IF NEW.adjusted_distance IS DISTINCT FROM OLD.adjusted_distance THEN changed_columns := changed_columns || 'AdjustedDistance'::TEXT; END IF;

        -- Skip an UPDATE that changed no column: a recorded row has every column null and reads downstream as cleared.
        IF changed_columns = '{}' THEN
            RETURN NEW;
        END IF;
    END IF;

    INSERT INTO public.training_sessions_outbox (
        operation, session_id, changed, recorded_on, type, steps, distance,
        duration, calories, post_processed_on, adjusted_steps, adjusted_distance)
    VALUES (
        CASE TG_OP WHEN 'INSERT' THEN 'I' ELSE 'U' END,
        NEW.id, changed_columns, NEW.recorded_on, NEW.type, NEW.steps, NEW.distance,
        NEW.duration, NEW.calories, NEW.post_processed_on, NEW.adjusted_steps, NEW.adjusted_distance);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- Emits a column if the change includes it. Inserts include every column, as in the SQL Server column-tracking example.
CREATE OR REPLACE FUNCTION web.get_training_sessions_sync(payload json)
RETURNS json AS $$
DECLARE
    from_version BIGINT  := COALESCE((payload ->> 'fromVersion')::BIGINT, 0);
    seeding      BOOLEAN := COALESCE(payload ->> 'mode', 'sync') = 'seed';
    horizon      xid8    := pg_snapshot_xmin(pg_current_snapshot());
    max_version  BIGINT;
    rows_json    json;
BEGIN
    SELECT COALESCE(MAX(id), from_version)
      INTO max_version
      FROM public.training_sessions_outbox
     WHERE xact_id < horizon;

    IF seeding THEN
        RETURN json_build_object(
            'Metadata', json_build_object('Sync', json_build_object(
                'Version', max_version, 'Type', 'Diff', 'ReasonCode', 0)),
            'Data', '[]'::json);
    END IF;

    SELECT COALESCE(json_agg(json_strip_nulls(json_build_object(
               '$operation',       o.operation,
               '$version',         o.id,
               -- Names of changed columns. A name in $changed with no key in the payload = the column was set to NULL (json_strip_nulls removes null keys).
               '$changed',         CASE WHEN o.operation = 'U' THEN o.changed END,
               -- NULL mask = the row predates column tracking: $changed is absent and every column is sent. Treat an update without $changed as a full row.
               'Id',               o.session_id,
               'RecordedOn',       CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'RecordedOn'       = ANY(o.changed) THEN o.recorded_on       END,
               'Type',             CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'Type'              = ANY(o.changed) THEN o.type              END,
               'Steps',            CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'Steps'             = ANY(o.changed) THEN o.steps             END,
               'Distance',         CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'Distance'          = ANY(o.changed) THEN o.distance          END,
               'Duration',         CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'Duration'          = ANY(o.changed) THEN o.duration          END,
               'Calories',         CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'Calories'          = ANY(o.changed) THEN o.calories          END,
               'PostProcessedOn',  CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'PostProcessedOn' = ANY(o.changed) THEN o.post_processed_on END,
               'AdjustedSteps',    CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'AdjustedSteps'    = ANY(o.changed) THEN o.adjusted_steps    END,
               'AdjustedDistance', CASE WHEN o.operation = 'I' OR o.changed IS NULL OR 'AdjustedDistance' = ANY(o.changed) THEN o.adjusted_distance END
           )) ORDER BY o.id), '[]'::json)
      INTO rows_json
      FROM public.training_sessions_outbox o
     WHERE o.id > from_version
       AND o.xact_id < horizon;

    RETURN json_build_object(
        'Metadata', json_build_object('Sync', json_build_object(
            'Version', max_version,
            'Type', CASE WHEN from_version = 0 THEN 'Full' ELSE 'Diff' END,
            'ReasonCode', 0)),
        'Data', rows_json);
END;
$$ LANGUAGE plpgsql SECURITY DEFINER;

ALTER FUNCTION web.get_training_sessions_sync(json) SET search_path = public, pg_temp;
GRANT EXECUTE ON FUNCTION web.get_training_sessions_sync(json) TO dotnetwebapp;

-- Payload semantics for consumers: https://melosso.github.io/trignis/reference/stored-procedure (PostgreSQL, Column-level tracking).
