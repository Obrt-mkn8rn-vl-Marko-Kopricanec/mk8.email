using Microsoft.EntityFrameworkCore;

namespace mk8.email.Infrastructure.Data;

public sealed partial class MailRuntimeSchemaService
{
    private const string FolderWriteGateFunction = """
        BEGIN
            -- MK8F:1 transaction namespace; taken before any scope/row locks.
            PERFORM pg_catalog.pg_advisory_xact_lock(1296775238, 1);
            RETURN NULL;
        END;
        """;

    private const string FolderWriteFunction = """
        DECLARE
            affected_accounts uuid[];
            affected_folders uuid[];
            account_id uuid;
        BEGIN
            IF TG_TABLE_NAME = 'folders' THEN
                affected_accounts := ARRAY[
                    CASE WHEN TG_OP <> 'INSERT' THEN OLD.inbox_id END,
                    CASE WHEN TG_OP <> 'DELETE' THEN NEW.inbox_id END];
            ELSE
                affected_folders := ARRAY[
                    CASE WHEN TG_OP <> 'INSERT' THEN OLD.folder_id END,
                    CASE WHEN TG_OP <> 'DELETE' THEN NEW.folder_id END];
                -- Keep scope stable during message moves, including cross-account moves.
                -- A cascading delete already holds the enclosing folder's account lock.
                SELECT array_agg(inbox_id) INTO affected_accounts FROM (
                    SELECT inbox_id FROM public.folders
                    WHERE id = ANY(affected_folders)
                    ORDER BY id FOR SHARE
                ) AS locked_folders;
            END IF;
            FOR account_id IN
                SELECT DISTINCT value FROM unnest(affected_accounts) AS scope(value)
                WHERE value IS NOT NULL ORDER BY value
            LOOP
                PERFORM 1 FROM public.inboxes WHERE id = account_id FOR NO KEY UPDATE;
            END LOOP;
            IF TG_OP = 'DELETE' THEN RETURN OLD; ELSE RETURN NEW; END IF;
        END;
        """;

    private async Task EnsureFolderWriteCoordinationAsync(CancellationToken cancellationToken)
    {
        // Do not repeatedly take DDL locks during ordinary on-demand Worker startup.
        if (await IsFolderWriteCoordinationReadyAsync(cancellationToken).ConfigureAwait(false)) return;
        await InstallFolderWriteCoordinationAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> IsFolderWriteCoordinationReadyAsync(CancellationToken cancellationToken) =>
        database.Database.SqlQuery<bool>($"""
            SELECT
                (SELECT count(*) FROM pg_catalog.pg_trigger t
                 WHERE NOT t.tgisinternal AND t.tgenabled = 'O' AND t.tgtype = 31
                   AND t.tgfoid = to_regprocedure('public.mk8_coordinate_folder_writes()')
                   AND ((t.tgname = 'mk8_folder_account_write_guard' AND t.tgrelid = 'public.folders'::regclass)
                     OR (t.tgname = 'mk8_email_account_write_guard' AND t.tgrelid = 'public.emails'::regclass))) = 2
                AND (SELECT count(*) FROM pg_catalog.pg_trigger t
                     WHERE NOT t.tgisinternal AND t.tgenabled = 'O'
                       AND t.tgfoid = to_regprocedure('public.mk8_gate_mail_writes()')
                       AND ((t.tgtype = 30 AND
                              ((t.tgname = 'mk8_folder_write_gate' AND t.tgrelid = 'public.folders'::regclass)
                            OR (t.tgname = 'mk8_email_write_gate' AND t.tgrelid = 'public.emails'::regclass)
                            OR (t.tgname = 'mk8_inbox_write_gate' AND t.tgrelid = 'public.inboxes'::regclass)))
                         OR (t.tgtype = 10 AND
                              ((t.tgname = 'mk8_user_delete_gate' AND t.tgrelid = 'public.users'::regclass)
                            OR (t.tgname = 'mk8_address_delete_gate' AND t.tgrelid = 'public.addresses'::regclass)
                            OR (t.tgname = 'mk8_company_delete_gate' AND t.tgrelid = 'public.companies'::regclass))))) = 6
                AND (SELECT count(*) FROM pg_catalog.pg_proc p
                    WHERE NOT p.prosecdef AND p.prorettype = 'trigger'::regtype
                      AND p.prolang = (SELECT oid FROM pg_catalog.pg_language WHERE lanname = 'plpgsql')
                      AND p.proconfig = ARRAY['search_path=pg_catalog, public']::text[]
                      AND ((p.oid = to_regprocedure('public.mk8_coordinate_folder_writes()') AND p.prosrc = {FolderWriteFunction})
                        OR (p.oid = to_regprocedure('public.mk8_gate_mail_writes()') AND p.prosrc = {FolderWriteGateFunction}))) = 2
                AS "Value"
            """).SingleAsync(cancellationToken);

    private async Task InstallFolderWriteCoordinationAsync(CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionLifetime = transaction.ConfigureAwait(false);
        await database.Database.ExecuteSqlRawAsync(
            "CREATE OR REPLACE FUNCTION public.mk8_coordinate_folder_writes() RETURNS trigger "
            + "LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog, public AS $mk8$"
            + FolderWriteFunction + "$mk8$;"
            + "CREATE OR REPLACE FUNCTION public.mk8_gate_mail_writes() RETURNS trigger "
            + "LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog, public AS $mk8$"
            + FolderWriteGateFunction + "$mk8$;"
            + """
            CREATE OR REPLACE TRIGGER mk8_folder_write_gate
                BEFORE INSERT OR UPDATE OR DELETE ON public.folders
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_email_write_gate
                BEFORE INSERT OR UPDATE OR DELETE ON public.emails
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_inbox_write_gate
                BEFORE INSERT OR UPDATE OR DELETE ON public.inboxes
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_user_delete_gate
                BEFORE DELETE ON public.users
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_address_delete_gate
                BEFORE DELETE ON public.addresses
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_company_delete_gate
                BEFORE DELETE ON public.companies
                FOR EACH STATEMENT EXECUTE FUNCTION public.mk8_gate_mail_writes();
            CREATE OR REPLACE TRIGGER mk8_folder_account_write_guard
                BEFORE INSERT OR UPDATE OR DELETE ON public.folders
                FOR EACH ROW EXECUTE FUNCTION public.mk8_coordinate_folder_writes();
            CREATE OR REPLACE TRIGGER mk8_email_account_write_guard
                BEFORE INSERT OR UPDATE OR DELETE ON public.emails
                FOR EACH ROW EXECUTE FUNCTION public.mk8_coordinate_folder_writes();
            """, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
