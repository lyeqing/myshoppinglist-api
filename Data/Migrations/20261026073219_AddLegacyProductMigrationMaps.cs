using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyProductMigrationMaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LegacyProductMigrationMaps",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    LegacyProductId = table.Column<long>(type: "bigint", nullable: false),
                    PrivateProductId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyProductMigrationMaps", x => new { x.UserAccountId, x.LegacyProductId });
                    table.ForeignKey(
                        name: "FK_LegacyProductMigrationMaps_Products_LegacyProductId",
                        column: x => x.LegacyProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyProductMigrationMaps_Products_PrivateProductId",
                        column: x => x.PrivateProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyProductMigrationMaps_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegacyShopProductMigrationMaps",
                columns: table => new
                {
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    LegacyShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    LegacyProductId = table.Column<long>(type: "bigint", nullable: false),
                    PrivateShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegacyShopProductMigrationMaps", x => new { x.UserAccountId, x.LegacyShopProductId });
                    table.ForeignKey(
                        name: "FK_LegacyShopProductMigrationMaps_LegacyProductMigrationMaps_U~",
                        columns: x => new { x.UserAccountId, x.LegacyProductId },
                        principalTable: "LegacyProductMigrationMaps",
                        principalColumns: new[] { "UserAccountId", "LegacyProductId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyShopProductMigrationMaps_ShopProducts_LegacyShopProdu~",
                        column: x => x.LegacyShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LegacyShopProductMigrationMaps_ShopProducts_PrivateShopProd~",
                        column: x => x.PrivateShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LegacyProductMigrationMaps_LegacyProductId",
                table: "LegacyProductMigrationMaps",
                column: "LegacyProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyProductMigrationMaps_PrivateProductId",
                table: "LegacyProductMigrationMaps",
                column: "PrivateProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegacyShopProductMigrationMaps_LegacyShopProductId",
                table: "LegacyShopProductMigrationMaps",
                column: "LegacyShopProductId");

            migrationBuilder.CreateIndex(
                name: "IX_LegacyShopProductMigrationMaps_PrivateShopProductId",
                table: "LegacyShopProductMigrationMaps",
                column: "PrivateShopProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LegacyShopProductMigrationMaps_UserAccountId_LegacyProductId",
                table: "LegacyShopProductMigrationMaps",
                columns: new[] { "UserAccountId", "LegacyProductId" });

            migrationBuilder.Sql("""
                CREATE FUNCTION "CheckLegacyProductMigrationMap"() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE source_id bigint; source_owner bigint; target_owner bigint;
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Migration receipts are immutable' USING ERRCODE = '23514'; END IF;
                    IF TG_OP = 'UPDATE' AND (NEW."UserAccountId", NEW."LegacyProductId", NEW."PrivateProductId")
                        IS DISTINCT FROM (OLD."UserAccountId", OLD."LegacyProductId", OLD."PrivateProductId") THEN
                        RAISE EXCEPTION 'Migration receipts are immutable' USING ERRCODE = '23514';
                    END IF;
                    EXECUTE format('SELECT "Id", "UserAccountId" FROM %I."Products" WHERE "Id" = $1 FOR KEY SHARE', TG_TABLE_SCHEMA)
                        INTO source_id, source_owner USING NEW."LegacyProductId";
                    EXECUTE format('SELECT "UserAccountId" FROM %I."Products" WHERE "Id" = $1 FOR KEY SHARE', TG_TABLE_SCHEMA)
                        INTO target_owner USING NEW."PrivateProductId";
                    IF source_id IS NULL OR source_owner IS NOT NULL OR target_owner IS DISTINCT FROM NEW."UserAccountId" THEN
                        RAISE EXCEPTION 'Migration receipts require a legacy source and same-account private copy' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER "TR_LegacyProductMigrationMaps_Owners" BEFORE INSERT OR UPDATE OR DELETE ON "LegacyProductMigrationMaps"
                    FOR EACH ROW EXECUTE FUNCTION "CheckLegacyProductMigrationMap"();

                CREATE FUNCTION "CheckLegacyShopProductMigrationMap"() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE source_id bigint; source_owner bigint; source_product bigint;
                    target_owner bigint; target_product bigint; expected_product bigint;
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'Migration receipts are immutable' USING ERRCODE = '23514'; END IF;
                    IF TG_OP = 'UPDATE' AND (NEW."UserAccountId", NEW."LegacyShopProductId", NEW."LegacyProductId", NEW."PrivateShopProductId")
                        IS DISTINCT FROM (OLD."UserAccountId", OLD."LegacyShopProductId", OLD."LegacyProductId", OLD."PrivateShopProductId") THEN
                        RAISE EXCEPTION 'Migration receipts are immutable' USING ERRCODE = '23514';
                    END IF;
                    EXECUTE format('SELECT "Id", "UserAccountId", "ProductId" FROM %I."ShopProducts" WHERE "Id" = $1 FOR KEY SHARE', TG_TABLE_SCHEMA)
                        INTO source_id, source_owner, source_product USING NEW."LegacyShopProductId";
                    EXECUTE format('SELECT "UserAccountId", "ProductId" FROM %I."ShopProducts" WHERE "Id" = $1 FOR KEY SHARE', TG_TABLE_SCHEMA)
                        INTO target_owner, target_product USING NEW."PrivateShopProductId";
                    EXECUTE format('SELECT "PrivateProductId" FROM %I."LegacyProductMigrationMaps" WHERE "UserAccountId" = $1 AND "LegacyProductId" = $2 FOR KEY SHARE', TG_TABLE_SCHEMA)
                        INTO expected_product USING NEW."UserAccountId", NEW."LegacyProductId";
                    IF source_id IS NULL OR source_owner IS NOT NULL OR source_product IS DISTINCT FROM NEW."LegacyProductId"
                        OR target_owner IS DISTINCT FROM NEW."UserAccountId" OR expected_product IS NULL OR target_product IS DISTINCT FROM expected_product THEN
                        RAISE EXCEPTION 'Retailer migration receipts must agree with product ownership and lineage' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER "TR_LegacyShopProductMigrationMaps_Owners" BEFORE INSERT OR UPDATE OR DELETE ON "LegacyShopProductMigrationMaps"
                    FOR EACH ROW EXECUTE FUNCTION "CheckLegacyShopProductMigrationMap"();

                CREATE FUNCTION "KeepMigratedShopProductParent"() RETURNS trigger LANGUAGE plpgsql AS $body$
                DECLARE invalid boolean;
                BEGIN
                    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I."LegacyShopProductMigrationMaps" m JOIN %I."LegacyProductMigrationMaps" p ON p."UserAccountId" = m."UserAccountId" AND p."LegacyProductId" = m."LegacyProductId" WHERE (m."LegacyShopProductId" = $1 AND m."LegacyProductId" <> $2) OR (m."PrivateShopProductId" = $1 AND p."PrivateProductId" <> $2))', TG_TABLE_SCHEMA, TG_TABLE_SCHEMA)
                        INTO invalid USING NEW."Id", NEW."ProductId";
                    IF invalid THEN RAISE EXCEPTION 'Cannot reparent a migrated retailer record' USING ERRCODE = '23514'; END IF;
                    RETURN NEW;
                END; $body$;
                CREATE TRIGGER "TR_ShopProducts_KeepMigrationParent" BEFORE UPDATE OF "ProductId" ON "ShopProducts"
                    FOR EACH ROW EXECUTE FUNCTION "KeepMigratedShopProductParent"();
                """);
            SetReferenceDeferral(migrationBuilder, true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $body$ BEGIN
                    IF EXISTS (SELECT 1 FROM "LegacyProductMigrationMaps") OR EXISTS (SELECT 1 FROM "LegacyShopProductMigrationMaps") THEN
                        RAISE EXCEPTION 'Cannot remove migration receipts after conversion; restore a backup or perform an explicit recovery';
                    END IF;
                END; $body$;
                DROP TRIGGER "TR_ShopProducts_KeepMigrationParent" ON "ShopProducts";
                DROP FUNCTION "KeepMigratedShopProductParent"();
                """);
            SetReferenceDeferral(migrationBuilder, false);
            migrationBuilder.DropTable(
                name: "LegacyShopProductMigrationMaps");

            migrationBuilder.DropTable(
                name: "LegacyProductMigrationMaps");
            migrationBuilder.Sql("DROP FUNCTION \"CheckLegacyShopProductMigrationMap\"(); DROP FUNCTION \"CheckLegacyProductMigrationMap\"();");
        }

        private static void SetReferenceDeferral(MigrationBuilder migrationBuilder, bool enabled)
        {
            var mode = enabled ? "DEFERRABLE INITIALLY IMMEDIATE" : "NOT DEFERRABLE";
            migrationBuilder.Sql($$"""
                DO $body$
                DECLARE fk record; found integer := 0;
                BEGIN
                    FOR fk IN SELECT c.conname, r.relname, n.nspname FROM pg_constraint c
                        JOIN pg_class r ON r.oid = c.conrelid JOIN pg_namespace n ON n.oid = r.relnamespace
                        JOIN pg_class parent ON parent.oid = c.confrelid JOIN pg_namespace pn ON pn.oid = parent.relnamespace
                        WHERE c.contype = 'f' AND n.nspname = current_schema() AND pn.nspname = n.nspname
                            AND parent.relname = 'ShoppingListProducts'
                            AND r.relname IN ('ProductImportJobs', 'PackDiscoveryJobs', 'BrandDiscoveryJobs', 'ShoppingListItemSubstitutions')
                    LOOP
                        EXECUTE format('ALTER TABLE %I.%I ALTER CONSTRAINT %I {{mode}}', fk.nspname, fk.relname, fk.conname);
                        found := found + 1;
                    END LOOP;
                    IF found <> 4 THEN RAISE EXCEPTION 'Expected four list-item reference constraints; migration stopped'; END IF;
                END; $body$;
                """);
        }
    }
}
