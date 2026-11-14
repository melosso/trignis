-- Function that Trignis calls: SELECT web.get_training_sessions_sync(@JsonParam::json). Run 01-change-tracking-setup.postgres.sql first. Must be a FUNCTION: Trignis reads the JSON from the first column of the first result.

CREATE OR REPLACE FUNCTION web.get_training_sessions_sync(payload json)
RETURNS json AS $$
DECLARE
    from_version BIGINT  := COALESCE((payload ->> 'fromVersion')::BIGINT, 0);
    seeding      BOOLEAN := COALESCE(payload ->> 'mode', 'sync') = 'seed';

    -- Oldest running transaction. Ids are assigned at INSERT and commit out of order: rows at or above the horizon are deferred to the next cycle, and MAX(id) alone skips uncommitted lower ids.
    horizon      xid8    := pg_snapshot_xmin(pg_current_snapshot());

    max_version  BIGINT;
    rows_json    json;
BEGIN
    -- The watermark uses the same horizon as the rows: MAX(id) without the horizon skips uncommitted rows.
    SELECT COALESCE(MAX(id), from_version)
      INTO max_version
      FROM public.training_sessions_outbox
     WHERE xact_id < horizon;

    -- Seeding means "start from here": report the version, send no history.
    IF seeding THEN
        RETURN json_build_object(
            'Metadata', json_build_object('Sync', json_build_object(
                'Version', max_version,
                'Type', 'Diff',
                'ReasonCode', 0)),
            'Data', '[]'::json);
    END IF;

    SELECT COALESCE(json_agg(json_build_object(
               '$operation',       o.operation,
               '$version',         o.id,
               'Id',               o.session_id,
               'RecordedOn',       o.recorded_on,
               'Type',             o.type,
               'Steps',            o.steps,
               'Distance',         o.distance,
               'Duration',         o.duration,
               'Calories',         o.calories,
               'PostProcessedOn',  o.post_processed_on,
               'AdjustedSteps',    o.adjusted_steps,
               'AdjustedDistance', o.adjusted_distance
           ) ORDER BY o.id), '[]'::json)
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

-- SECURITY DEFINER runs as the owner: row-level security on the tracked table does not hide changes. A fixed search_path prevents injection.
ALTER FUNCTION web.get_training_sessions_sync(json) SET search_path = public, pg_temp;

GRANT EXECUTE ON FUNCTION web.get_training_sessions_sync(json) TO dotnetwebapp;

-- Environment file example and outbox cleanup: https://melosso.github.io/trignis/reference/stored-procedure (PostgreSQL).
