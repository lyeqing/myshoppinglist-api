param([Parameter(Mandatory=$true)][string]$PostgresBin)
$ErrorActionPreference='Stop'
# This rehearsal is deliberately restricted to the dedicated local test instance.
if($env:PGHOST -ne '127.0.0.1' -or $env:PGPORT -ne '55432' -or $env:PGDATABASE -notmatch '^MyShoppingList.*Test$' -or -not $env:PGPASSWORD -or -not $env:MYSHOPPINGLIST_TEST_CONNECTION){throw 'Select the isolated local test database and PG credentials first.'}
$repo=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$suffix=[guid]::NewGuid().ToString('N')
$schema='codex_backup_'+$suffix
$restore='MyShoppingListRestore_'+$suffix
$backupRoot=Join-Path $repo '.local-backups'
[IO.Directory]::CreateDirectory($backupRoot) | Out-Null
$backup=Join-Path $backupRoot ($schema+'.dump')
$psql=Join-Path $PostgresBin 'psql.exe'
$createdSchema=$false; $createdDatabase=$false
function Sql([string]$database,[string]$statement){
    $result=("SET client_min_messages=warning;`n"+$statement) | & $psql -X -w -qAt -v ON_ERROR_STOP=1 -d $database
    if($LASTEXITCODE -ne 0){throw 'Backup rehearsal SQL failed.'}
    return ($result -join "`n")
}
function Fingerprint([string]$database){
    $query=@"
BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SELECT format('SELECT json_build_object(''table'',%L,''rows'',count(*),''hash'',md5(COALESCE(string_agg(md5(to_jsonb(t)::text),'''' ORDER BY md5(to_jsonb(t)::text) COLLATE "C"),''''))) FROM %I.%I t;',tablename,schemaname,tablename)
FROM pg_tables WHERE schemaname='$schema' ORDER BY tablename COLLATE "C"
\gexec
COMMIT;
"@
    return Sql $database $query
}
try {
    Sql $env:PGDATABASE ('CREATE SCHEMA "'+$schema+'";') | Out-Null; $createdSchema=$true
    $env:ConnectionStrings__MyShoppingList=$env:MYSHOPPINGLIST_TEST_CONNECTION+';Search Path='+$schema+';Pooling=false'
    $env:UserExtension__PersonalCapturesEnabled='true'; $env:AiIntegration__Enabled='false'; $env:ExtensionBridge__Enabled='false'; $env:Operations__Enabled='false'
    Push-Location $repo
    try { & dotnet ef database update --no-build | Out-Null; if($LASTEXITCODE -ne 0){throw 'Disposable backup schema migration failed.'} }
    finally { Pop-Location }
    $seed=@"
SET search_path TO "$schema";
INSERT INTO "UserAccounts" ("Email","PasswordHash","PasswordSalt","DisplayName","IsTrial","IsActive","IsPaid","ContributionEnabled","ContributionBlocked","CreatedDate","UpdatedDate")
VALUES ('backup-fixture@example.test','fixture','fixture','Backup fixture',false,true,false,false,false,now(),now());
INSERT INTO "UserAccounts" ("Email","PasswordHash","PasswordSalt","DisplayName","IsTrial","IsActive","IsPaid","ContributionEnabled","ContributionBlocked","CreatedDate","UpdatedDate")
VALUES ('other-backup-fixture@example.test','other-private-password-fixture','other-private-salt-fixture','Other private fixture',false,true,false,false,false,now(),now());
INSERT INTO "FeedbackTickets" ("Id","UserAccountId","RequestId","RequestHash","Category","Message","Component","Release","Status","Version","CreatedAt","UpdatedAt")
SELECT gen_random_uuid(),"Id",gen_random_uuid(),repeat('A',64),'bug',CASE WHEN "Id"=1 THEN 'Disposable restore verification' ELSE 'Other private feedback fixture' END,'website','test','new',1,now(),now() FROM "UserAccounts";
INSERT INTO "BillingCustomers" ("UserAccountId","CustomerId","SnapshotJson")
SELECT "Id",CASE WHEN "Id"=1 THEN 'cus_export_fixture' ELSE 'cus_other_fixture' END,'{}'::jsonb FROM "UserAccounts";
INSERT INTO "BillingWebhookEvents" ("Id","CustomerId","Type","ReceivedAt","NextAttemptAt","Attempts","Status")
SELECT 'evt_export_'||"UserAccountId","CustomerId",'invoice.paid',now(),now(),0,'pending' FROM "BillingCustomers";
"@
    Sql $env:PGDATABASE $seed | Out-Null
    $export=Join-Path $backupRoot ($schema+'-account.json')
    & python (Join-Path $PSScriptRoot 'export_account.py') --psql $psql --schema $schema --account-id 1 --output $export | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Read-only account export rehearsal failed.'}
    $exportData=[IO.File]::ReadAllText($export)
    if($exportData.Contains('cus_other_fixture') -or $exportData.Contains('evt_export_2') -or -not $exportData.Contains('evt_export_1')){throw 'Billing export customer ownership failed.'}
    if($exportData.Contains('other-backup-fixture@example.test') -or $exportData.Contains('other-private-password-fixture') -or $exportData.Contains('Other private feedback fixture') -or $exportData.Contains('PasswordHash') -or -not $exportData.Contains('Disposable restore verification')){throw 'Account export isolation or secret-redaction failed.'}
    $before=Fingerprint $env:PGDATABASE
    & (Join-Path $PostgresBin 'pg_dump.exe') -w -Fc --no-owner --no-privileges --schema=$schema --file=$backup $env:PGDATABASE
    if($LASTEXITCODE -ne 0){throw 'Backup dump failed.'}
    Sql $env:PGDATABASE ('CREATE DATABASE "'+$restore+'";') | Out-Null; $createdDatabase=$true
    & (Join-Path $PostgresBin 'pg_restore.exe') -w --exit-on-error --no-owner --no-privileges --dbname=$restore $backup
    if($LASTEXITCODE -ne 0){throw 'Restore failed.'}
    $after=Fingerprint $restore
    if($before -cne $after){throw 'Restored table fingerprints differ from the source.'}
    [pscustomobject]@{result='passed';allTableFingerprintsMatch=$true;backupFile=$backup;disposableOnly=$true} | ConvertTo-Json -Compress
}
finally {
    if($schema -notmatch '^codex_backup_[a-f0-9]{32}$' -or $restore -notmatch '^MyShoppingListRestore_[a-f0-9]{32}$'){throw 'Refusing unsafe cleanup.'}
    if($createdDatabase){Sql $env:PGDATABASE ('DROP DATABASE "'+$restore+'";') | Out-Null}
    if($createdSchema){Sql $env:PGDATABASE ('DROP SCHEMA "'+$schema+'" CASCADE;') | Out-Null}
}
