// SPDX-License-Identifier: MPL-2.0

using Workspaces.Common.Contracts;

namespace Workspaces.Common.Validation;

public sealed class ValidationResult
{
    private readonly List<ErrorDetail> _errors = [];

    public IReadOnlyList<ErrorDetail> Errors => _errors;
    public bool IsValid => _errors.Count == 0;

    public void Add(string field, string message) => _errors.Add(new ErrorDetail(field, message));
}
