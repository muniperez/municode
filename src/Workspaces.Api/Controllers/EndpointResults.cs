using Microsoft.EntityFrameworkCore;
using Workspaces.Api.Auth;
using Workspaces.Common.Contracts;
using Workspaces.Common.Data;
using Workspaces.Common.Services;

namespace Workspaces.Api.Controllers;

public static class EndpointResults
{
    public static ControlPlanePrincipal RequirePrincipal(HttpContext http) =>
        http.User.GetControlPlanePrincipal() ?? throw new InvalidOperationException("Authenticated control-plane principal was not resolved.");

    public static IResult ValidationProblem(string message, Workspaces.Common.Validation.ValidationResult validation) =>
        Results.BadRequest(new ErrorResponse(new ErrorBody("validation_failed", message, validation.Errors)));

    public static IResult Forbidden() =>
        Results.Json(new ErrorResponse(new ErrorBody("forbidden", "You are not allowed to access this resource.", [])), statusCode: StatusCodes.Status403Forbidden);

    public static IResult NotFound(string code, string message) =>
        Results.NotFound(new ErrorResponse(new ErrorBody(code, message, [])));

    public static IResult Conflict(string code, string message) =>
        Results.Conflict(new ErrorResponse(new ErrorBody(code, message, [])));

    public static IResult Problem(string code, string message, int statusCode) =>
        Results.Json(new ErrorResponse(new ErrorBody(code, message, [])), statusCode: statusCode);
}
