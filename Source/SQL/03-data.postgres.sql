/*
    Sample activity for the PostgreSQL setup, mirroring 03-data.sqlserver.sql.
    Run 01-change-tracking-setup.postgres.sql and one of the 02 scripts first.
*/

/*
    Insert some "pre-existing" data
*/
INSERT INTO public.training_sessions
    (recorded_on, type, steps, distance, duration, calories)
VALUES
    ('2021-10-28 17:27:23 -08:00', 'Run', 3784, 5123, 32*60+3, 526),
    ('2021-10-27 17:54:48 -08:00', 'Run',    0, 4981, 32*60+37, 480);

/*
    View Data
*/
SELECT * FROM public.training_sessions;

/*
    Make some changes
*/
INSERT INTO public.training_sessions
    (recorded_on, type, steps, distance, duration, calories)
VALUES
    ('2021-10-26 18:24:32 -08:00', 'Run', 4866, 4562, 30*60+18, 475);

INSERT INTO public.training_sessions
    (recorded_on, type, steps, distance, duration, calories)
VALUES
    ('2021-10-26 18:24:32 -08:00', 'Run', 4866, 4562, 30*60+18, 475);

UPDATE public.training_sessions
   SET steps = steps + 1
 WHERE id = (SELECT MAX(id) FROM public.training_sessions);

/*
    Delete something
*/
DELETE FROM public.training_sessions
 WHERE id = (SELECT MAX(id) FROM public.training_sessions);

/*
    What Trignis would receive on its next cycle, starting from nothing
*/
SELECT jsonb_pretty(web.get_training_sessions_sync('{"fromVersion":0,"mode":"sync"}'::json)::jsonb);
