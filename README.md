# Public API Extractor

Public API Extractor is a small .NET command-line tool that outputs the specification of the public API surface of any managed portable executable file with CLI metadata, such as a managed .dll, a managed .exe, a reference assembly, a facade assembly, or a compatible metadata-only PE. Native-only PE files are rejected.

The output is designed for three overlapping use cases:

- **LLM-friendly API maps**: compact enough to paste into a model context while preserving the relationships needed to understand an API.
- **Human review**: readable one-line records for types, constructors, fields, properties, events, methods, enum members, and type forwarders.
- **Regression snapshots**: stable text output that can be committed and diffed when public API shape changes.

It is especially useful for inspecting .NET reference assemblies, facade assemblies, and public metadata surfaces without decompiling implementation bodies.

## What the tool emits

The extractor emits a line-oriented API specification. The format favors concise C#-like syntax while preserving metadata facts that matter for API compatibility and usage.

It currently includes:

- Assembly and module identity.
- Public and externally visible types.
- Type forwarders / exported type forwarders.
- Constructors, methods, properties, events, fields, and enum members.
- Generic parameters and constraints.
- Operators and conversion operators.
- Extension methods, including the `this` receiver marker.
- Nullable reference annotations, including nested generic nullability.
- Ref, `ref readonly`, pointer, array, span/memory-oriented, and function-pointer-like signatures where available through metadata.
- Named tuple syntax from `TupleElementNamesAttribute`.
- Semantic attributes that affect API usage, such as `Obsolete`, `RequiresUnreferencedCode`, `RequiresDynamicCode`, `DynamicallyAccessedMembers`, `StringSyntax`, `AllowNull`, `DisallowNull`, and `NotNullWhen`.
- Symbolic enum default values where possible.
- Constant fields, including special values such as `double.NaN`, infinities, and escaped character literals.
- Compact aliases for repeated long attributes.
- Diagnostics for assemblies that contain metadata but expose no public API records.

The extractor intentionally does **not** emit implementation bodies, IL, private members, or compiler-generated implementation details that do not contribute to the public API contract.

## Requirements

- .NET SDK 8.0 or later.
- The project references:
  - `System.Reflection.Metadata`
  - `System.Collections.Immutable`

The current project targets `net8.0` and uses C# nullable annotations.

## Build

From the project directory:

```bash
dotnet build -c Release
```

## Basic usage

The tool writes the API specification to standard output. Redirect stdout to save the specification:

```bash
PublicApiExtractor.exe path/to/SomeAssembly.dll > SomeAssemblySpecification.txt
```

The program exits with:

- `0` on success.
- `1` if extraction fails.
- `2` if the command-line arguments are invalid.



## Output format overview

The output begins with a compact header:

```text
# K:T=type C=ctor M=method P=prop F=field V=event E=enum-member X=type-forward
# visibility omitted means public; @ sets namespace until next @
# repeated long attributes may be emitted as [ATn] aliases declared by # attr lines
# assembly System.Reflection.Metadata, Version=10.0.0.0, Culture=neutral, PublicKeyToken=...
# module System.Reflection.Metadata.dll
# namespaces-used System System.Collections.Generic System.Collections.Immutable ...
# aref A0 System.Collections.Immutable, Version=...
# tref R0 A0:System.Collections.Immutable.ImmutableArray`1
# attr AT0 [RequiresUnreferencedCode("...")]
```

Record kinds:

| Prefix | Meaning |
|---|---|
| `@` | Current namespace section |
| `T` | Type declaration |
| `C` | Constructor |
| `M` | Method |
| `P` | Property |
| `F` | Field |
| `V` | Event |
| `E` | Enum member |
| `X` | Exported type / type forwarder |

Visibility is omitted for normal public members and included only when it adds useful information, such as `protected`, `protected-internal`, or `private-protected`.

## Example output

```text
# K:T=type C=ctor M=method P=prop F=field V=event E=enum-member X=type-forward
# visibility omitted means public; @ sets namespace until next @
# assembly System.Net.Http, Version=10.0.0.0, Culture=neutral, PublicKeyToken=...
# module System.Net.Http.dll

@ System.Net.Http

T class HttpClient : HttpMessageInvoker
 C HttpClient()
 C HttpClient(HttpMessageHandler handler)
 P Uri? BaseAddress get set
 P TimeSpan Timeout get set
 M Task<HttpResponseMessage> GetAsync(Uri? requestUri)
 M Task<HttpResponseMessage> PostAsync(Uri? requestUri,HttpContent? content)
 M Task<HttpResponseMessage> PostAsync([StringSyntax(Uri)] string? requestUri,HttpContent? content)
 M Task<string> GetStringAsync(Uri? requestUri)
 M void CancelPendingRequests()
```

Facade assemblies may primarily emit type forwarders:

```text
X forward System.Action -> System.Private.CoreLib, Version=...
X forward System.Action<T> -> System.Private.CoreLib, Version=...
X forward System.Collections.Generic.IEnumerable<T> -> System.Private.CoreLib, Version=...
```

Assemblies with metadata but no detected public API are explicitly marked:

```text
# metadata type-definitions=1 exported-types=0 public-type-definitions=0 public-exported-types=0
# no-public-api
```

## Design goals

### Concise but semantic

The output is intentionally denser than C# source. It avoids method bodies, XML documentation, private implementation details, and redundant compiler infrastructure. At the same time, it preserves public API details that materially affect how callers use the assembly.

### Stable and diffable

Records are ordered deterministically. This makes generated files suitable for snapshot-style API review and regression testing.

### Metadata-aware

The extractor uses `System.Reflection.Metadata` directly rather than reflection loading the assembly. This avoids executing user code and works well for reference assemblies and metadata-only assemblies.

### LLM-friendly

The format is built to be readable by both humans and language models. It avoids decompiler noise while keeping enough information to reason about overloads, generics, nullability, attributes, type relationships, and forwarders.

## Reference assemblies vs implementation assemblies

For supported public API contracts, prefer **reference assemblies** when available. Reference assemblies contain metadata that represents the public surface without implementation bodies.

Implementation assemblies are still useful for exploration, but they may expose public metadata that is not intended to be the stable supported contract. Assemblies such as `System.Private.CoreLib.dll` and `System.Private.Xml.dll` are excellent extractor stress tests, but reference assemblies are usually the right input for official API snapshots.

## Suggested regression corpus

A useful regression corpus should include APIs that exercise nullability, async, tuples, ref returns, forwarders, semantic attributes, spans, pointers, constants, and overload-heavy generic APIs.

Good examples include:

```text
System.Net.Http.dll
System.Private.CoreLib.dll
System.Memory.dll
System.Collections.Immutable.dll
System.Runtime.dll
System.Reflection.Metadata.dll
System.Linq.dll
System.Text.Json.dll
System.Security.Cryptography.dll
System.Runtime.InteropServices.dll
System.Diagnostics.Process.dll
System.Diagnostics.DiagnosticSource.dll
System.Net.Sockets.dll
System.Net.Security.dll
System.Text.RegularExpressions.dll
```

## Development notes

The current code is intentionally small and direct. Key areas:

| File | Purpose |
|---|---|
| `Program.cs` | CLI entry point. |
| `PublicApiExtractor.cs` | Main metadata traversal and extraction orchestration. |
| `CanonicalApiWriter.cs` | Deterministic text writer and attribute aliasing. |
| `MetadataSignatureProvider.cs` | SRM signature decoding into renderable type names. |
| `SignatureTypeName.cs` | Structured signature/type rendering model. |
| `MetadataNames.cs` | Type names, literals, attributes, escaping, and formatting helpers. |
| `VisibilityPolicy.cs` | Public/protected visibility decisions. |
| `ConstantDecoder.cs` | Constant value decoding and literal rendering. |
| `MetadataNamePolicy.cs` | Name shortening and public-signature dependency tracking. |