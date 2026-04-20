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
CB0006  | ConfigBoundNET | Warning  | [Range] applied to non-numeric property
CB0007  | ConfigBoundNET | Warning  | Length attribute applied to non-string property
CB0008  | ConfigBoundNET | Error    | [RegularExpression] pattern is not a valid regex
CB0009  | ConfigBoundNET | Info     | [Required] is redundant on non-nullable property
CB0010  | ConfigBoundNET | Warning  | Property type is not bindable by ConfigBoundNET
CB0011  | ConfigBoundNET | Info     | Nested [ConfigSection] type defined outside this compilation
