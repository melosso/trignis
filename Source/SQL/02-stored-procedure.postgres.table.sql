-- The function Trignis calls. Run 01-change-tracking-setup.postgres.sql first.
--
-- Note: this must be a FUNCTION, not a PROCEDURE. A PostgreSQL procedure cannot return a
-- value, and Trignis reads the JSON document from the first column of the first result.
--
-- Trignis invokes it as:  SELECT web.get_training_sessions_sync(@JsonParam::json)

CREATE OR REPLACE FUNCTION web.get_training_sessions_sync(payload json)
RETURNS json AS $$
DECLARE
    from_version BIGINT  := COALESCE((payload ->> 'fromVersion')::BIGINT, 0);
    seeding      BOOLEAN := COALESCE(payload ->> 'mode', 'sync') = 'seed';

    -- Oldest transaction still running. Anything committed by an older transaction is
    -- visible for good; anything at or above it may still be in flight.
    --
    -- This is the part that is easy to get wrong. An identity column is assigned at INSERT,
    -- not at COMMIT, so ids become visible out of order. Storing plain MAX(id) would skip
    -- any lower id still uncommitted, permanently. Filtering on the horizon defers those
    -- rows to the next cycle instead, in order.
    horizon      xid8    := pg_snapshot_xmin(pg_current_snapshot());

    max_version  BIGINT;
    rows_json    json;
BEGIN
    -- The watermark must come through the same horizon as the rows. Using the horizon for
    -- the rows but MAX(id) for the version reintroduces exactly the gap it prevents.
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

-- SECURITY DEFINER runs as the owner, so row-level security on the tracked table cannot
-- silently hide changes from Trignis. Pin the search path or that becomes an injection route.
ALTER FUNCTION web.get_training_sessions_sync(json) SET search_path = public, pg_temp;

GRANT EXECUTE ON FUNCTION web.get_training_sessions_sync(json) TO dotnetwebapp;

-- Matching environment file:
--
-- {
--   "Provider": "postgres",
--   "ConnectionStrings": {
--     "PrimaryDatabase": "Host=localhost;Database=primary;Username=dotnetwebapp;Password=..."
--   },
--   "ChangeTracking": {
--     "ExportToFile": true,
--     "TrackingObjects": [
--       {
--         "Name": "TrainingSessions",
--         "Database": "PrimaryDatabase",
--         "TableName": "public.training_sessions",
--         "StoredProcedureName": "web.get_training_sessions_sync"
--       }
--     ]
--   }
-- }
--
-- The outbox grows until you trim it. Trignis cannot do that for you, because it does not
-- know which consumer is furthest behind. Delete below the version shown on the dashboard,
-- keeping a margin, and never above it:
--
--   DELETE FROM public.training_sessions_outbox WHERE id <= <last processed version> - 100000;
