-- One database per service, each owned by its own login role and closed to every other role. A service cannot
-- read another service's tables even by mistake, which is the boundary docs/requirements.md asks for. In
-- production these would be separate servers; one container keeps the dev machine within its memory.
--
-- The roles are spelled out NOSUPERUSER NOBYPASSRLS (and no CREATEDB or CREATEROLE) even though those are the
-- defaults, so a reader does not have to remember the defaults to see that row-level security would bind them.
--
-- Each password is the role name. These are public development values for a stack bound to localhost, not
-- secrets, and they must never be reused anywhere else.
--
-- Runs once, on the first start of an empty volume (docker-entrypoint-initdb.d). After editing this file, reset
-- the volume as docs/operations.md describes.

\set ON_ERROR_STOP on

-- Nobody but the superuser needs the maintenance databases.
revoke connect on database postgres from public;
revoke connect on database template1 from public;

create role stays login password 'stays' nosuperuser nobypassrls nocreatedb nocreaterole;
create database stays owner stays;
revoke all on database stays from public;

create role cars login password 'cars' nosuperuser nobypassrls nocreatedb nocreaterole;
create database cars owner cars;
revoke all on database cars from public;

create role flights login password 'flights' nosuperuser nobypassrls nocreatedb nocreaterole;
create database flights owner flights;
revoke all on database flights from public;

create role booking login password 'booking' nosuperuser nobypassrls nocreatedb nocreaterole;
create database booking owner booking;
revoke all on database booking from public;

create role payments login password 'payments' nosuperuser nobypassrls nocreatedb nocreaterole;
create database payments owner payments;
revoke all on database payments from public;

create role airlinesim login password 'airlinesim' nosuperuser nobypassrls nocreatedb nocreaterole;
create database airlinesim owner airlinesim;
revoke all on database airlinesim from public;
