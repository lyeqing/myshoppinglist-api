using myshoppinglist_api.Providers.Models;
using System.Text.Json.Serialization;

namespace myshoppinglist_api.Contracts;

public sealed record ResearchSubmission(Guid RequestId, string Query, string WorkflowVersion,
    ResearchCandidateInput[] Candidates, ResearchIssueInput[]? Issues = null);
public sealed record ResearchCandidateInput(string Key, string Retailer, string Url, ProductIdentity Identity,
    string Evidence, DateTimeOffset ObservedAt, decimal? Price = null, string Currency = "AUD",
    string PriceScope = "Unknown",
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] long? ShopLocationId = null,
    string? Promotion = null, string Match = "uncertain",
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] long? RelatedProductId = null,
    int? AiConfidence = null,
    TissueAttributes? Tissue = null);
public sealed record TissueAttributes(int? Rolls = null, int? SheetsPerRoll = null, int? Ply = null,
    decimal? SheetWidthMm = null, decimal? SheetLengthMm = null);
public sealed record ResearchIssueInput(string Retailer, string Code, string Detail);
public sealed record ResearchCandidateView(Guid Id, ResearchCandidateInput Report, string Validation,
    string[] Issues, string PriceVerification, decimal? ComparableUnitPrice, string? UnitBasis,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)] long? SelectedProductId);
public sealed record ResearchIssueView(Guid Id, string Retailer, string Code, string Detail, string Status,
    int Revision, string? Resolution, string Target = "");
public sealed record ResearchReview(Guid Id, string Query, string WorkflowVersion, string Status,
    DateTime CreatedAt, ResearchCandidateView[] Candidates, ResearchIssueView[] Issues);
public sealed record ResearchJobSummary(Guid Id, string Query, string Status, DateTime CreatedAt, int OpenIssues);
public sealed record ResearchSelection(Guid RequestId, Guid CandidateId, long ListId, int Quantity);
public sealed record ResearchIssueUpdate(Guid RequestId, int ExpectedRevision, string Status, string Reason);
public sealed record ResearchRefreshTask(string MappingId, string ProductId, string Name, string Retailer,
    string Url, string Reason, DateTime? LastObservedAt, DateTime? DueAt, decimal? PreviousPrice,
    string Currency, string PriceScope, string? LocationId);
