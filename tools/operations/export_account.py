"""Prepare a private, read-only support export. Never exports auth secrets.

The operator must verify the support request, review coverage and use an approved
secure delivery channel. This is not automatic account deletion or email delivery.
PG credentials come from PG* environment variables; no credentials are printed.
"""
import argparse
import hashlib
import json
import re
import subprocess
from pathlib import Path

REDACT = "password|token|claim|hash|salt|authorizationid|installationid|issuereference|restrictionreason|errormessage|administratorid|^reason$"


def export_sql(schema, account):
    if not re.fullmatch(r"[a-z][a-z0-9_]{0,62}", schema) or not isinstance(account, int) or account <= 0 or account > 9223372036854775807:
        raise ValueError("Use a valid schema and positive account ID.")
    redact = f"(SELECT COALESCE(jsonb_object_agg(key,value),'{{}}'::jsonb) FROM jsonb_each(to_jsonb(t)) WHERE key !~* '{REDACT}')"
    sql = f'''BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
SET LOCAL search_path TO "{schema}";
SELECT jsonb_build_object('table','Account','rows',COALESCE(jsonb_agg({redact}),'[]'::jsonb)) FROM "UserAccounts" t WHERE "Id"={account};
SELECT format($cmd$SELECT jsonb_build_object('table',%L,'rows',COALESCE(jsonb_agg({redact}),'[]'::jsonb)) FROM %I.%I t WHERE "UserAccountId"={account};$cmd$,table_name,table_schema,table_name)
FROM information_schema.columns WHERE table_schema='{schema}' AND column_name='UserAccountId' ORDER BY table_name
\\gexec
'''
    for table, condition in [
        ("ShoppingListProducts", f't."ShoppingListId" IN (SELECT "Id" FROM "ShoppingLists" WHERE "UserAccountId"={account})'),
        ("ProductImportRetailerResults", f't."ProductImportJobId" IN (SELECT "Id" FROM "ProductImportJobs" WHERE "UserAccountId"={account})'),
        ("ProductBrandSuggestions", f't."BrandDiscoveryJobId" IN (SELECT "Id" FROM "BrandDiscoveryJobs" WHERE "UserAccountId"={account})'),
        ("AccountAdminAudits", f't."AccountId"={account}'),
        ("BillingWebhookEvents", f't."CustomerId" IN (SELECT "CustomerId" FROM "BillingCustomers" WHERE "UserAccountId"={account})'),
    ]:
        sql += f'''SELECT jsonb_build_object('table','{table}','rows',COALESCE(jsonb_agg({redact}),'[]'::jsonb)) FROM "{table}" t WHERE {condition};\n'''
    return sql + "COMMIT;\n"


def prepare(psql, schema, account, output, database=None):
    command = [str(psql), "-X", "-w", "-qAt", "-v", "ON_ERROR_STOP=1"]
    if database:
        command += ["-d", database]
    result = subprocess.run(command, input=export_sql(schema, account), text=True, capture_output=True, timeout=60)
    if result.returncode:
        raise RuntimeError("Export query failed. No partial export was written; inspect the private database service separately.")
    datasets = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    account_rows = next((d["rows"] for d in datasets if d["table"] == "Account"), [])
    if len(account_rows) != 1:
        raise ValueError("Account was not found. No export was written.")
    data = {"version": 1, "accountId": account, "supportReviewRequired": True, "datasets": datasets,
        "coverage": "Direct account ownership and explicit list/import/brand children. Review historical shared catalog references, staff-only security records and external providers separately."}
    payload = json.dumps(data, ensure_ascii=False, sort_keys=True, indent=2).encode("utf8")
    output = Path(output).resolve()
    # Refuse overwriting an existing export. The directory must be prepared by the operator.
    with output.open("xb") as stream:
        stream.write(payload)
    return {"output": str(output), "sha256": hashlib.sha256(payload).hexdigest(), "datasets": len(datasets), "rows": sum(len(d["rows"]) for d in datasets), "supportReviewRequired": True}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--psql", default="psql")
    parser.add_argument("--schema", default="public")
    parser.add_argument("--database")
    parser.add_argument("--account-id", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(prepare(args.psql, args.schema, args.account_id, args.output, args.database)))


if __name__ == "__main__":
    main()
