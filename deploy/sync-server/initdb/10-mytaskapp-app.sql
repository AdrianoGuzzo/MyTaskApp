-- O papel da API e das migrations (SEC-020, docs/sync-server.md secao 5): sem
-- superusuario, dono do banco e do schema public. O mytaskapp do POSTGRES_USER
-- e o bootstrap superuser e segue so para o initdb e a manutencao.
--
-- O PostgreSQL roda este arquivo na primeira subida, com o volume vazio. Quem ja
-- subiu roda o mesmo arquivo a mao (secao 5): ele pode rodar de novo, e passa
-- para o papel as tabelas que as migrations criaram com o mytaskapp.

\set ON_ERROR_STOP on
\set app_password `cat /run/secrets/postgres_app_password`
SELECT current_database() AS db \gset

SELECT 'CREATE ROLE mytaskapp_app'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'mytaskapp_app') \gexec

ALTER ROLE mytaskapp_app WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
    PASSWORD :'app_password';
ALTER DATABASE :"db" OWNER TO mytaskapp_app;
ALTER SCHEMA public OWNER TO mytaskapp_app;

-- O REASSIGN OWNED BY mytaskapp nao serve: o bootstrap e dono dos objetos do
-- sistema. As sequencias de identity seguem a tabela (e recusam o ALTER
-- SEQUENCE), entao so as avulsas vao a parte.
DO $$
DECLARE
    obj record;
BEGIN
    FOR obj IN
        SELECT c.oid::regclass AS name,
               CASE c.relkind
                   WHEN 'v' THEN 'VIEW'
                   WHEN 'm' THEN 'MATERIALIZED VIEW'
                   WHEN 'S' THEN 'SEQUENCE'
                   ELSE 'TABLE'
               END AS kind
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE n.nspname = 'public'
          AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
          AND pg_get_userbyid(c.relowner) <> 'mytaskapp_app'
          AND NOT (c.relkind = 'S' AND EXISTS (
              SELECT FROM pg_depend d
              WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid AND d.deptype IN ('a', 'i')))
    LOOP
        EXECUTE format('ALTER %s %s OWNER TO mytaskapp_app', obj.kind, obj.name);
    END LOOP;
END
$$;
