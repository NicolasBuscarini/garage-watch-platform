using System.Security.Claims;
using GarageWatch.Api.Contracts;
using GarageWatch.Api.Errors;
using GarageWatch.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace GarageWatch.Api.Endpoints.Admin;

public sealed class GetFaceExtractorRegistryEndpoint() : BaseEndpoint("Face extractor", "AdminGetFaceExtractorRegistry",
    "Get face extractor candidates", "Returns versioned model metadata and the operator selection; selection does not activate runtime or sample collection.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapGet("/api/v1/admin/face-extractor-registry", async (FaceExtractorRegistryService registry,
            CancellationToken cancellationToken) => Results.Ok(await registry.GetAsync(cancellationToken)))
            .RequireAuthorization("Administrator").Produces<FaceExtractorRegistrySnapshot>()
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class CreateFaceExtractorCandidateEndpoint() : BaseEndpoint("Face extractor", "AdminCreateFaceExtractorCandidate",
    "Register face extractor candidate", "Stores metadata and separate code/weights license references without fetching artifacts or enabling inference.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPost("/api/v1/admin/face-extractor-candidates", async (
            CreateFaceExtractorCandidateRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, FaceExtractorRegistryService registry,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                var candidate = await registry.CreateCandidateAsync(actor.Id,
                    new FaceExtractorCandidateInput(request.ModelId, request.ModelVersion, request.ArtifactSha256,
                        request.SourceUri, request.CodeLicense, request.WeightsLicense, request.AdapterId,
                        request.AdapterContractVersion), cancellationToken);
                return Results.Created("/api/v1/admin/face-extractor-registry", candidate);
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_face_extractor_candidate")); }
            catch (FaceExtractorConflictException exception) { return Results.Conflict(new ApiErrorResponse(exception.Message)); }
        }).RequireAuthorization("Administrator").Produces<FaceExtractorCandidateSummary>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}

public sealed class SelectFaceExtractorEndpoint() : BaseEndpoint("Face extractor", "AdminSelectFaceExtractor",
    "Select face extractor for evaluation", "Selects a version-pinned candidate for evaluation only; no runtime or collection capability is activated.")
{
    protected override RouteHandlerBuilder MapCore(IEndpointRouteBuilder routes) =>
        routes.MapPut("/api/v1/admin/face-extractor-selection", async (
            SelectFaceExtractorRequest request, HttpContext context, ClaimsPrincipal principal,
            UserManager<ApplicationUser> users, FaceExtractorRegistryService registry,
            CancellationToken cancellationToken) =>
        {
            if (!EndpointSecurity.IsSameOrigin(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await users.GetUserAsync(principal);
            if (actor is null) return Results.Unauthorized();
            try
            {
                return Results.Ok(await registry.SelectForEvaluationAsync(actor.Id,
                    request.CandidatePublicId ?? string.Empty, request.ExpectedVersion, cancellationToken));
            }
            catch (ArgumentException) { return Results.BadRequest(new ApiErrorResponse("invalid_face_extractor_selection")); }
            catch (KeyNotFoundException) { return Results.NotFound(new ApiErrorResponse("face_extractor_candidate_not_found")); }
            catch (FaceExtractorConflictException exception) { return Results.Conflict(new ApiErrorResponse(exception.Message)); }
        }).RequireAuthorization("Administrator").Produces<FaceExtractorSelectionResult>()
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized).Produces(StatusCodes.Status403Forbidden);
}
