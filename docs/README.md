# ConfigBoundNET Documentation

## Guides

- **[Getting Started](getting-started.md)** — install, first config type, register in DI, section name inference
- **[Migration from Manual Options](migration-from-manual-options.md)** — step-by-step conversion from hand-written `IOptions<T>` + `IValidateOptions<T>` to ConfigBoundNET

## Reference

- **[Configuration Binding](configuration-binding.md)** — supported types, collections, nested configs, unsupported types
- **[Validation](validation.md)** — nullability checks, DataAnnotations, custom hooks, nested validation, execution order
- **[Diagnostics Reference](diagnostics.md)** — all CB0001–CB0010 diagnostics with examples, fixes, and suppression
- **[AOT and Trimming](aot-and-trimming.md)** — how the reflection-free pipeline works, CI verification, known limitations

## Deep dives

- **[Architecture](architecture.md)** — internal pipeline, project structure, file-by-file guide
- **[Design Philosophy](design-philosophy.md)** — the five principles, trade-offs, what ConfigBoundNET is and isn't
- **[Examples](examples.md)** — walkthrough of both example projects with feature matrix
