; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category       | Severity | Notes
--------|----------------|----------|-------------------------------------------
CB0001  | ConfigBoundNET | Error    | Config type must be declared partial
CB0002  | ConfigBoundNET | Error    | Configuration section name cannot be empty
CB0003  | ConfigBoundNET | Error    | Nested config types are not supported
CB0004  | ConfigBoundNET | Warning  | Config type has no bindable properties
CB0005  | ConfigBoundNET | Error    | Unsupported target for [ConfigSection]
