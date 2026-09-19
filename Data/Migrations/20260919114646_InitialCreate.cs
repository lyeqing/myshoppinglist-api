using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace myshoppinglist_api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Brand = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Description = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ItemDetail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    GTIN = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    ManufacturerPartNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ModelNumber = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Variant = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PackQuantity = table.Column<int>(type: "integer", nullable: true),
                    PackSize = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    PackUnit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SubCategory = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                    table.CheckConstraint("CK_Products_PackQuantity", "\"PackQuantity\" IS NULL OR \"PackQuantity\" > 0");
                    table.CheckConstraint("CK_Products_PackSize", "\"PackSize\" IS NULL OR \"PackSize\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "Shops",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Website = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    LogoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Shops", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordSalt = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsTrial = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccounts", x => x.Id);
                    table.CheckConstraint("CK_UserAccounts_Credentials", "(\"IsTrial\" AND \"ExpiresDate\" IS NOT NULL AND \"Email\" IS NULL AND \"PasswordHash\" IS NULL AND \"PasswordSalt\" IS NULL) OR (NOT \"IsTrial\" AND \"Email\" IS NOT NULL AND \"PasswordHash\" IS NOT NULL AND \"PasswordSalt\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "ShopLocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    StoreCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Address1 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Address2 = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Suburb = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    State = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Postcode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Latitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    Longitude = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopLocations", x => x.Id);
                    table.CheckConstraint("CK_ShopLocations_Latitude", "\"Latitude\" BETWEEN -90 AND 90");
                    table.CheckConstraint("CK_ShopLocations_Longitude", "\"Longitude\" BETWEEN -180 AND 180");
                    table.ForeignKey(
                        name: "FK_ShopLocations_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShopProducts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    ShopProductCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ShopSku = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    NameAtShop = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DescriptionAtShop = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: true),
                    ProductUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ImageUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    GTIN = table.Column<string>(type: "character varying(14)", maxLength: 14, nullable: true),
                    MatchType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MatchConfidence = table.Column<int>(type: "integer", nullable: true),
                    FirstFoundDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastFoundDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastCheckedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProducts", x => x.Id);
                    table.UniqueConstraint("AK_ShopProducts_Id_ShopId", x => new { x.Id, x.ShopId });
                    table.CheckConstraint("CK_ShopProducts_MatchConfidence", "\"MatchConfidence\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_ShopProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopProducts_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShoppingLists",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShoppingLists", x => x.Id);
                    table.UniqueConstraint("AK_ShoppingLists_Id_UserAccountId", x => new { x.Id, x.UserAccountId });
                    table.ForeignKey(
                        name: "FK_ShoppingLists_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopProductPriceHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopLocationId = table.Column<long>(type: "bigint", nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NormalPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SpecialType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SpecialDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SpecialStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SpecialEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PriceScope = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CheckedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductPriceHistory", x => x.Id);
                    table.CheckConstraint("CK_ShopProductPriceHistory_Price", "\"Price\" >= 0 AND (\"NormalPrice\" IS NULL OR \"NormalPrice\" >= 0) AND (\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0)");
                    table.CheckConstraint("CK_ShopProductPriceHistory_SpecialDates", "\"SpecialStartDate\" IS NULL OR \"SpecialEndDate\" IS NULL OR \"SpecialEndDate\" >= \"SpecialStartDate\"");
                    table.CheckConstraint("CK_ShopProductPriceHistory_StoreScope", "\"PriceScope\" <> 'StoreSpecific' OR \"ShopLocationId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ShopProductPriceHistory_ShopLocations_ShopLocationId",
                        column: x => x.ShopLocationId,
                        principalTable: "ShopLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopProductPriceHistory_ShopProducts_ShopProductId",
                        column: x => x.ShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShopProductPrices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShopProductId = table.Column<long>(type: "bigint", nullable: false),
                    ShopLocationId = table.Column<long>(type: "bigint", nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    NormalPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    SpecialType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SpecialDescription = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SpecialStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SpecialEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InStock = table.Column<bool>(type: "boolean", nullable: true),
                    PriceScope = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CheckedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopProductPrices", x => x.Id);
                    table.CheckConstraint("CK_ShopProductPrices_Price", "\"Price\" >= 0 AND (\"NormalPrice\" IS NULL OR \"NormalPrice\" >= 0) AND (\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0)");
                    table.CheckConstraint("CK_ShopProductPrices_SpecialDates", "\"SpecialStartDate\" IS NULL OR \"SpecialEndDate\" IS NULL OR \"SpecialEndDate\" >= \"SpecialStartDate\"");
                    table.CheckConstraint("CK_ShopProductPrices_StoreScope", "\"PriceScope\" <> 'StoreSpecific' OR \"ShopLocationId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_ShopProductPrices_ShopLocations_ShopLocationId",
                        column: x => x.ShopLocationId,
                        principalTable: "ShopLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShopProductPrices_ShopProducts_ShopProductId",
                        column: x => x.ShopProductId,
                        principalTable: "ShopProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ShoppingListProducts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ShoppingListId = table.Column<long>(type: "bigint", nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsPurchased = table.Column<bool>(type: "boolean", nullable: false),
                    PurchasedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    PreferredShopId = table.Column<long>(type: "bigint", nullable: true),
                    AddedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShoppingListProducts", x => x.Id);
                    table.UniqueConstraint("AK_ShoppingListProducts_Id_ShoppingListId_ProductId", x => new { x.Id, x.ShoppingListId, x.ProductId });
                    table.CheckConstraint("CK_ShoppingListProducts_Quantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ShoppingListProducts_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ShoppingListProducts_ShoppingLists_ShoppingListId",
                        column: x => x.ShoppingListId,
                        principalTable: "ShoppingLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ShoppingListProducts_Shops_PreferredShopId",
                        column: x => x.PreferredShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductImportJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ShoppingListId = table.Column<long>(type: "bigint", nullable: false),
                    SourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    NormalisedSourceUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    RequestedQuantity = table.Column<int>(type: "integer", nullable: false),
                    SourceShopId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProgressStage = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProductId = table.Column<long>(type: "bigint", nullable: true),
                    ShoppingListProductId = table.Column<long>(type: "bigint", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastActivityDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductImportJobs", x => x.Id);
                    table.CheckConstraint("CK_ProductImportJobs_AttemptCount", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_ProductImportJobs_Claim", "(\"Status\" = 'Processing' AND \"ClaimToken\" IS NOT NULL AND \"LeaseExpiresDate\" IS NOT NULL) OR (\"Status\" <> 'Processing' AND \"ClaimToken\" IS NULL AND \"LeaseExpiresDate\" IS NULL)");
                    table.CheckConstraint("CK_ProductImportJobs_ListItem", "\"ShoppingListProductId\" IS NULL OR \"ProductId\" IS NOT NULL");
                    table.CheckConstraint("CK_ProductImportJobs_Quantity", "\"RequestedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ProductImportJobs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductImportJobs_ShoppingListProducts_ShoppingListProductI~",
                        columns: x => new { x.ShoppingListProductId, x.ShoppingListId, x.ProductId },
                        principalTable: "ShoppingListProducts",
                        principalColumns: new[] { "Id", "ShoppingListId", "ProductId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductImportJobs_ShoppingLists_ShoppingListId_UserAccountId",
                        columns: x => new { x.ShoppingListId, x.UserAccountId },
                        principalTable: "ShoppingLists",
                        principalColumns: new[] { "Id", "UserAccountId" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductImportJobs_Shops_SourceShopId",
                        column: x => x.SourceShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductImportJobs_UserAccounts_UserAccountId",
                        column: x => x.UserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductImportRetailerResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductImportJobId = table.Column<long>(type: "bigint", nullable: false),
                    ShopId = table.Column<long>(type: "bigint", nullable: false),
                    ShopProductId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MatchType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    MatchConfidence = table.Column<int>(type: "integer", nullable: true),
                    IsFromCache = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CheckedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StartedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductImportRetailerResults", x => x.Id);
                    table.CheckConstraint("CK_ProductImportRetailerResults_MatchConfidence", "\"MatchConfidence\" BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "FK_ProductImportRetailerResults_ProductImportJobs_ProductImpor~",
                        column: x => x.ProductImportJobId,
                        principalTable: "ProductImportJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProductImportRetailerResults_ShopProducts_ShopProductId_Sho~",
                        columns: x => new { x.ShopProductId, x.ShopId },
                        principalTable: "ShopProducts",
                        principalColumns: new[] { "Id", "ShopId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductImportRetailerResults_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Shops",
                columns: new[] { "Id", "Code", "CreatedDate", "Domain", "IsActive", "LogoUrl", "Name", "UpdatedDate", "Website" },
                values: new object[,]
                {
                    { 1L, "coles", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "coles.com.au", true, null, "Coles", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "https://www.coles.com.au" },
                    { 2L, "woolworths", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "woolworths.com.au", true, null, "Woolworths", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "https://www.woolworths.com.au" },
                    { 3L, "aldi", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "aldi.com.au", true, null, "ALDI", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "https://www.aldi.com.au" },
                    { 4L, "iga", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "iga.com.au", true, null, "IGA", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "https://www.iga.com.au" },
                    { 5L, "foodland", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "foodlandsa.com.au", true, null, "Foodland", new DateTime(2026, 9, 19, 0, 0, 0, 0, DateTimeKind.Utc), "https://www.foodlandsa.com.au" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_ProductId",
                table: "ProductImportJobs",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_ShoppingListId_Status",
                table: "ProductImportJobs",
                columns: new[] { "ShoppingListId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_ShoppingListId_UserAccountId",
                table: "ProductImportJobs",
                columns: new[] { "ShoppingListId", "UserAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_ShoppingListProductId_ShoppingListId_Prod~",
                table: "ProductImportJobs",
                columns: new[] { "ShoppingListProductId", "ShoppingListId", "ProductId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_SourceShopId",
                table: "ProductImportJobs",
                column: "SourceShopId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_Status_LeaseExpiresDate",
                table: "ProductImportJobs",
                columns: new[] { "Status", "LeaseExpiresDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_Status_NextAttemptDate_CreatedDate",
                table: "ProductImportJobs",
                columns: new[] { "Status", "NextAttemptDate", "CreatedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportJobs_UserAccountId",
                table: "ProductImportJobs",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportRetailerResults_ProductImportJobId_ShopId",
                table: "ProductImportRetailerResults",
                columns: new[] { "ProductImportJobId", "ShopId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportRetailerResults_ShopId",
                table: "ProductImportRetailerResults",
                column: "ShopId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductImportRetailerResults_ShopProductId_ShopId",
                table: "ProductImportRetailerResults",
                columns: new[] { "ShopProductId", "ShopId" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_Brand_ManufacturerPartNumber_ModelNumber",
                table: "Products",
                columns: new[] { "Brand", "ManufacturerPartNumber", "ModelNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_GTIN",
                table: "Products",
                column: "GTIN");

            migrationBuilder.CreateIndex(
                name: "IX_ShopLocations_ShopId_StoreCode",
                table: "ShopLocations",
                columns: new[] { "ShopId", "StoreCode" },
                unique: true,
                filter: "\"StoreCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListProducts_PreferredShopId",
                table: "ShoppingListProducts",
                column: "PreferredShopId");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListProducts_ProductId",
                table: "ShoppingListProducts",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListProducts_ShoppingListId_ProductId",
                table: "ShoppingListProducts",
                columns: new[] { "ShoppingListId", "ProductId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingLists_ExpiresDate",
                table: "ShoppingLists",
                column: "ExpiresDate");

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingLists_UserAccountId",
                table: "ShoppingLists",
                column: "UserAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPriceHistory_ShopLocationId",
                table: "ShopProductPriceHistory",
                column: "ShopLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPriceHistory_ShopProductId_CheckedDate",
                table: "ShopProductPriceHistory",
                columns: new[] { "ShopProductId", "CheckedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPrices_ShopLocationId",
                table: "ShopProductPrices",
                column: "ShopLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPrices_ShopProductId_PriceScope_Currency",
                table: "ShopProductPrices",
                columns: new[] { "ShopProductId", "PriceScope", "Currency" },
                unique: true,
                filter: "\"ShopLocationId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProductPrices_ShopProductId_ShopLocationId_PriceScope_C~",
                table: "ShopProductPrices",
                columns: new[] { "ShopProductId", "ShopLocationId", "PriceScope", "Currency" },
                unique: true,
                filter: "\"ShopLocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_ProductId_ShopId",
                table: "ShopProducts",
                columns: new[] { "ProductId", "ShopId" });

            migrationBuilder.CreateIndex(
                name: "IX_ShopProducts_ShopId_ShopProductCode",
                table: "ShopProducts",
                columns: new[] { "ShopId", "ShopProductCode" },
                unique: true,
                filter: "\"ShopProductCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Shops_Code",
                table: "Shops",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Shops_Domain",
                table: "Shops",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_Email",
                table: "UserAccounts",
                column: "Email",
                unique: true,
                filter: "\"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccounts_IsTrial_ExpiresDate",
                table: "UserAccounts",
                columns: new[] { "IsTrial", "ExpiresDate" });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_ExpiresDate",
                table: "UserSessions",
                column: "ExpiresDate");

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_TokenHash",
                table: "UserSessions",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserAccountId",
                table: "UserSessions",
                column: "UserAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductImportRetailerResults");

            migrationBuilder.DropTable(
                name: "ShopProductPriceHistory");

            migrationBuilder.DropTable(
                name: "ShopProductPrices");

            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropTable(
                name: "ProductImportJobs");

            migrationBuilder.DropTable(
                name: "ShopLocations");

            migrationBuilder.DropTable(
                name: "ShopProducts");

            migrationBuilder.DropTable(
                name: "ShoppingListProducts");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "ShoppingLists");

            migrationBuilder.DropTable(
                name: "Shops");

            migrationBuilder.DropTable(
                name: "UserAccounts");
        }
    }
}
