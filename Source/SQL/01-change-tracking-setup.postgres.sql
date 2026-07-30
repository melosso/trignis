-- Sets up a PostgreSQL database for Trignis: a role, the tracked table, and the outbox
-- that makes its changes visible.
--
-- Note: PostgreSQL has no built-in equivalent of SQL Server change tracking. There is nothing
-- to switch on at the database level. Instead, a trigger writes every change to an outbox
-- table, whose BIGSERIAL id becomes the version Trignis tracks.
--
-- Requires PostgreSQL 13 or newer for pg_current_xact_id() and pg_snapshot_xmin().

-- Create a role for the application if it does not exist
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'dotnetwebapp') THEN
        CREATE ROLE dotnetwebapp LOGIN PASSWORD 'a987REALLY#$%TRONGpa44w0rd!';
    END IF;
END
$$;

-- Schema holding the functions Trignis calls, mirroring the "web" schema in the SQL Server script
CREATE SCHEMA IF NOT EXISTS web;
GRANT USAGE ON SCHEMA web TO dotnetwebapp;

-- The tracked table
DROP TABLE IF EXISTS public.training_sessions CASCADE;
CREATE TABLE public.training_sessions
(
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    recorded_on       TIMESTAMPTZ    NOT NULL,
    type              VARCHAR(50)    NOT NULL,
    steps             INT            NOT NULL,
    distance          INT            NOT NULL, -- Meters
    duration          INT            NOT NULL, -- Seconds
    calories          INT            NOT NULL,
    post_processed_on TIMESTAMPTZ,
    adjusted_steps    INT,
    adjusted_distance NUMERIC(9, 6)
);

-- The outbox. Its id is the version Trignis stores; xact_id is what keeps concurrent
-- inserts from being skipped. See 02-stored-procedure.postgres.sql.
DROP TABLE IF EXISTS public.training_sessions_outbox;
CREATE TABLE public.training_sessions_outbox
(
    id                BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    xact_id           xid8           NOT NULL DEFAULT pg_current_xact_id(),
    operation         CHAR(1)        NOT NULL,
    session_id        BIGINT         NOT NULL,
    recorded_on       TIMESTAMPTZ,
    type              VARCHAR(50),
    steps             INT,
    distance          INT,
    duration          INT,
    calories          INT,
    post_processed_on TIMESTAMPTZ,
    adjusted_steps    INT,
    adjusted_distance NUMERIC(9, 6)
);

CREATE INDEX training_sessions_outbox_xact_idx ON public.training_sessions_outbox (xact_id, id);

-- One trigger covering all three operations. DELETE has to read OLD, because the base row
-- is already gone by the time anything else could look for it.
CREATE OR REPLACE FUNCTION public.training_sessions_capture() RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        INSERT INTO public.training_sessions_outbox (operation, session_id)
        VALUES ('D', OLD.id);
        RETURN OLD;
    END IF;

    INSERT INTO public.training_sessions_outbox (
        operation, session_id, recorded_on, type, steps, distance,
        duration, calories, post_processed_on, adjusted_steps, adjusted_distance)
    VALUES (
        CASE TG_OP WHEN 'INSERT' THEN 'I' ELSE 'U' END,
        NEW.id, NEW.recorded_on, NEW.type, NEW.steps, NEW.distance,
        NEW.duration, NEW.calories, NEW.post_processed_on, NEW.adjusted_steps, NEW.adjusted_distance);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS training_sessions_capture ON public.training_sessions;
CREATE TRIGGER training_sessions_capture
AFTER INSERT OR UPDATE OR DELETE ON public.training_sessions
FOR EACH ROW EXECUTE FUNCTION public.training_sessions_capture();

-- Trignis only ever calls the function, so it needs no rights on the tables themselves.
-- The function is SECURITY DEFINER in 02, which also sidesteps row-level security.
GRANT SELECT ON public.training_sessions_outbox TO dotnetwebapp;
