-- Column-level tracking for PostgreSQL. Run 01-change-tracking-setup.postgres.sql first.
--
-- This is the PostgreSQL counterpart to 02-stored-procedure.sqlserver.column.sql. Where SQL
-- Server exposes a change mask you read with CHANGE_TRACKING_IS_COLUMN_IN_MASK, PostgreSQL
-- has no such thing, so the trigger works out which columns actually changed and records
-- their names alongside the row.
--
-- Like the SQL Server version, this is opt-in: the table-level script is the simpler default,
-- and you only need this one when your consumers care about distinguishing "unchanged" from
-- "set to null".

-- Add the change mask to the outbox created by 01
ALTER TABLE public.training_sessions_outbox
    ADD COLUMN IF NOT EXISTS changed TEXT[];

-- Replace the capture trigger with one that records which columns moved.
-- IS DISTINCT FROM is used rather than <> so that a change to or from NULL counts,
-- which a plain inequality would quietly miss.
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
        IF NEW.recorded_on       IS DISTINCT FROM OLD.recorded_on       THEN changed_columns := changed_columns || 'recorded_on'::TEXT;       END IF;
        IF NEW.type              IS DISTINCT FROM OLD.type              THEN changed_columns := changed_columns || 'type'::TEXT;              END IF;
        IF NEW.steps             IS DISTINCT FROM OLD.steps             THEN changed_columns := changed_columns || 'steps'::TEXT;             END IF;
        IF NEW.distance          IS DISTINCT FROM OLD.distance          THEN changed_columns := changed_columns || 'distance'::TEXT;          END IF;
        IF NEW.duration          IS DISTINCT FROM OLD.duration          THEN changed_columns := changed_columns || 'duration'::TEXT;          END IF;
        IF NEW.calories          IS DISTINCT FROM OLD.calories          THEN changed_columns := changed_columns || 'calories'::TEXT;          END IF;
        IF NEW.post_processed_on IS DISTINCT FROM OLD.post_processed_on THEN changed_columns := changed_columns || 'post_processed_on'::TEXT; END IF;
        IF NEW.adjusted_steps    IS DISTINCT FROM OLD.adjusted_steps    THEN changed_columns := changed_columns || 'adjusted_steps'::TEXT;    END IF;
        IF NEW.adjusted_distance IS DISTINCT FROM OLD.adjusted_distance THEN changed_columns := changed_columns || 'adjusted_distance'::TEXT; END IF;

        -- An UPDATE that changed nothing still fires the trigger. Recording it would send
        -- a row with every column null, which reads downstream as "everything was cleared".
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

-- Emits a column only when the change actually touched it. Inserts carry every column,
-- matching how the SQL Server column-tracking example treats SYS_CHANGE_OPERATION = 'I'.
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
               -- Ground truth for which columns moved. Without this a consumer cannot tell a
               -- column that was set to NULL from one that was never touched, because
               -- json_strip_nulls removes both. Absent from the payload but present here
               -- means the value was cleared.
               '$changed',         CASE WHEN o.operation = 'U' THEN o.changed END,
               'Id',               o.session_id,
               'RecordedOn',       CASE WHEN o.operation = 'I' OR 'recorded_on'       = ANY(o.changed) THEN o.recorded_on       END,
               'Type',             CASE WHEN o.operation = 'I' OR 'type'              = ANY(o.changed) THEN o.type              END,
               'Steps',            CASE WHEN o.operation = 'I' OR 'steps'             = ANY(o.changed) THEN o.steps             END,
               'Distance',         CASE WHEN o.operation = 'I' OR 'distance'          = ANY(o.changed) THEN o.distance          END,
               'Duration',         CASE WHEN o.operation = 'I' OR 'duration'          = ANY(o.changed) THEN o.duration          END,
               'Calories',         CASE WHEN o.operation = 'I' OR 'calories'          = ANY(o.changed) THEN o.calories          END,
               'PostProcessedOn',  CASE WHEN o.operation = 'I' OR 'post_processed_on' = ANY(o.changed) THEN o.post_processed_on END,
               'AdjustedSteps',    CASE WHEN o.operation = 'I' OR 'adjusted_steps'    = ANY(o.changed) THEN o.adjusted_steps    END,
               'AdjustedDistance', CASE WHEN o.operation = 'I' OR 'adjusted_distance' = ANY(o.changed) THEN o.adjusted_distance END
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

-- Reading the payload downstream:
--
--   $operation = 'I'  every column is present, this is the full row
--   $operation = 'U'  $changed lists the columns that moved. For each name in that list,
--                     a matching key in the payload is the new value, and a missing key
--                     means the column was set to NULL.
--   $operation = 'D'  only Id is present, the row is gone
--
-- Columns absent from both the payload and $changed did not change, so leave them alone.
-- Sending NULL for an unchanged column instead would be the same mistake as
-- INCLUDE_NULL_VALUES with column tracking on SQL Server: the consumer can no longer
-- distinguish "untouched" from "cleared", and quietly wipes data it was never told to.
