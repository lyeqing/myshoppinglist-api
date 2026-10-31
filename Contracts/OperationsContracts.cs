using System.Text.Json.Serialization;
namespace myshoppinglist_api.Contracts;

public sealed record FeedbackSubmission([property: JsonRequired] long ExpectedAccountId, Guid RequestId, string Category,
    string Message, int? Rating = null, string Component = "website", byte[]? Screenshot = null, bool ScreenshotApproved = false);
public sealed record FeedbackUpdate(long ExpectedVersion, string Status, string? Response, string? IssueReference, string Reason);
public sealed record FeedbackView(Guid Id, long AccountId, string Category, string Message, int? Rating, string Component,
    string Status, string? Response, string? IssueReference, bool HasScreenshot, long Version, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record VisitorEvent(Guid EventId, Guid VisitorId, Guid SessionId, string Name, string Page, string? Attribution, bool Consent);
public sealed record DiagnosticEvent(string Component, string Code, string Release);
public sealed record IncidentSimulation(string Component, bool Healthy, string Reason);
