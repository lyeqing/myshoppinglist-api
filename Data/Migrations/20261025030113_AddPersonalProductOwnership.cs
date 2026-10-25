using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPersonalProductOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ShopProducts_ShopId_ShopProductCode",
                table: "ShopProducts");

            migrationBuilder.AddColumn<long>(
                name: "UserAccountId",
                table: "ShopProducts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserAccountId",
                table: "ShopProductPrices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserAccountId",
                table: "ShopProductPriceHistory",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UserAccountId",
                table: "Products",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_ShopId_ShopProductCode",
                table: "ShopProducts",
                columns: new[] { "ShopId", "ShopProductCode" },
                unique: true,
                filter: "\"ShopProductCode\" IS NOT NULL AND \"UserAccountId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_UserAccountId_ShopId_ProductUrl",
                table: "ShopProducts",
                columns: new[] { "UserAccountId", "ShopId", "ProductUrl" },
                unique: true,
                filter: "\"UserAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_UserAccountId_ShopId_ShopProductCode",
                table: "ShopProducts",
                columns: new[] { "UserAccountId", "ShopId", "ShopProductCode" },
                unique: true,
                filter: "\"ShopProductCode\" IS NOT NULL AND \"UserAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPrices_UserAccountId_ShopProductId",
                table: "ShopProductPrices",
                columns: new[] { "UserAccountId", "ShopProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPriceHistory_UserAccountId_ShopProductId_Checked~",
                table: "ShopProductPriceHistory",
                columns: new[] { "UserAccountId", "ShopProductId", "CheckedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserAccountId_Brand_ManufacturerPartNumber_ModelNu~",
                table: "Products",
                columns: new[] { "UserAccountId", "Brand", "ManufacturerPartNumber", "ModelNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_UserAccountId_GTIN",
                table: "Products",
                columns: new[] { "UserAccountId", "GTIN" });

            migrationBuilder.AddForeignKey(
                name: "FK_Products_UserAccounts_UserAccountId",
                table: "Products",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
            // Nullable owners preserve legacy rows. A normal nullable composite FK
            // would skip validation for a null owner, allowing legacy/private links.
            // Check both directions of that boundary and prevent owner reassignment.
            migrationBuilder.Sql("""
                CREATE FUNCTION "CheckPersonalProductParentOwner"() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                DECLARE parent_owner bigint;
                BEGIN
                    EXECUTE format('SELECT "UserAccountId" FROM %I.%I WHERE "Id" = $1 FOR KEY SHARE',
                        TG_TABLE_SCHEMA, TG_ARGV[0])
                        INTO parent_owner USING (to_jsonb(NEW)->>TG_ARGV[1])::bigint;
                    IF parent_owner IS DISTINCT FROM NEW."UserAccountId" THEN
                        RAISE EXCEPTION 'Product records must have the same owner as their parent'
                            USING ERRCODE = '23514', CONSTRAINT = 'CK_PersonalProduct_ParentOwner';
                    END IF;
                    RETURN NEW;
                END;
                $body$;

                CREATE FUNCTION "KeepPersonalProductOwner"() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW."UserAccountId" IS DISTINCT FROM OLD."UserAccountId" THEN
                        RAISE EXCEPTION 'Product ownership is immutable; migrate by creating a private copy'
                            USING ERRCODE = '23514', CONSTRAINT = 'CK_PersonalProduct_ImmutableOwner';
                    END IF;
                    RETURN NEW;
                END;
                $body$;

                CREATE TRIGGER "TR_ShopProducts_ParentOwner"
                    BEFORE INSERT OR UPDATE OF "ProductId", "UserAccountId" ON "ShopProducts"
                    FOR EACH ROW EXECUTE FUNCTION "CheckPersonalProductParentOwner"('Products', 'ProductId');
                CREATE TRIGGER "TR_ShopProductPrices_ParentOwner"
                    BEFORE INSERT OR UPDATE OF "ShopProductId", "UserAccountId" ON "ShopProductPrices"
                    FOR EACH ROW EXECUTE FUNCTION "CheckPersonalProductParentOwner"('ShopProducts', 'ShopProductId');
                CREATE TRIGGER "TR_ShopProductPriceHistory_ParentOwner"
                    BEFORE INSERT OR UPDATE OF "ShopProductId", "UserAccountId" ON "ShopProductPriceHistory"
                    FOR EACH ROW EXECUTE FUNCTION "CheckPersonalProductParentOwner"('ShopProducts', 'ShopProductId');

                CREATE TRIGGER "TR_Products_ImmutableOwner"
                    BEFORE UPDATE OF "UserAccountId" ON "Products"
                    FOR EACH ROW EXECUTE FUNCTION "KeepPersonalProductOwner"();
                CREATE TRIGGER "TR_ShopProducts_ImmutableOwner"
                    BEFORE UPDATE OF "UserAccountId" ON "ShopProducts"
                    FOR EACH ROW EXECUTE FUNCTION "KeepPersonalProductOwner"();
                CREATE TRIGGER "TR_ShopProductPrices_ImmutableOwner"
                    BEFORE UPDATE OF "UserAccountId" ON "ShopProductPrices"
                    FOR EACH ROW EXECUTE FUNCTION "KeepPersonalProductOwner"();
                CREATE TRIGGER "TR_ShopProductPriceHistory_ImmutableOwner"
                    BEFORE UPDATE OF "UserAccountId" ON "ShopProductPriceHistory"
                    FOR EACH ROW EXECUTE FUNCTION "KeepPersonalProductOwner"();
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopProductPriceHistory_UserAccounts_UserAccountId",
                table: "ShopProductPriceHistory",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopProductPrices_UserAccounts_UserAccountId",
                table: "ShopProductPrices",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShopProducts_UserAccounts_UserAccountId",
                table: "ShopProducts",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Never silently discard ownership or merge accounts during rollback.
            migrationBuilder.Sql("""
                DO $body$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "Products" WHERE "UserAccountId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "ShopProducts" WHERE "UserAccountId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "ShopProductPrices" WHERE "UserAccountId" IS NOT NULL)
                        OR EXISTS (SELECT 1 FROM "ShopProductPriceHistory" WHERE "UserAccountId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot remove ownership while private product data exists; restore a backup or migrate it explicitly';
                    END IF;
                END;
                $body$;
                DROP TRIGGER "TR_ShopProducts_ParentOwner" ON "ShopProducts";
                DROP TRIGGER "TR_ShopProductPrices_ParentOwner" ON "ShopProductPrices";
                DROP TRIGGER "TR_ShopProductPriceHistory_ParentOwner" ON "ShopProductPriceHistory";
                DROP TRIGGER "TR_Products_ImmutableOwner" ON "Products";
                DROP TRIGGER "TR_ShopProducts_ImmutableOwner" ON "ShopProducts";
                DROP TRIGGER "TR_ShopProductPrices_ImmutableOwner" ON "ShopProductPrices";
                DROP TRIGGER "TR_ShopProductPriceHistory_ImmutableOwner" ON "ShopProductPriceHistory";
                DROP FUNCTION "CheckPersonalProductParentOwner"();
                DROP FUNCTION "KeepPersonalProductOwner"();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_Products_UserAccounts_UserAccountId",
                table: "Products");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopProductPriceHistory_UserAccounts_UserAccountId",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopProductPrices_UserAccounts_UserAccountId",
                table: "ShopProductPrices");

            migrationBuilder.DropForeignKey(
                name: "FK_ShopProducts_UserAccounts_UserAccountId",
                table: "ShopProducts");

            migrationBuilder.DropIndex(
                name: "IX_ShopProducts_ShopId_ShopProductCode",
                table: "ShopProducts");

            migrationBuilder.DropIndex(
                name: "IX_ShopProducts_UserAccountId_ShopId_ProductUrl",
                table: "ShopProducts");

            migrationBuilder.DropIndex(
                name: "IX_ShopProducts_UserAccountId_ShopId_ShopProductCode",
                table: "ShopProducts");

            migrationBuilder.DropIndex(
                name: "IX_ShopProductPrices_UserAccountId_ShopProductId",
                table: "ShopProductPrices");

            migrationBuilder.DropIndex(
                name: "IX_ShopProductPriceHistory_UserAccountId_ShopProductId_Checked~",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropIndex(
                name: "IX_Products_UserAccountId_Brand_ManufacturerPartNumber_ModelNu~",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_UserAccountId_GTIN",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "ShopProducts");

            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "ShopProductPrices");

            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "ShopProductPriceHistory");

            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "Products");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_ShopId_ShopProductCode",
                table: "ShopProducts",
                columns: new[] { "ShopId", "ShopProductCode" },
                unique: true,
                filter: "\"ShopProductCode\" IS NOT NULL");
        }
    }
}
