# Audible contributor mapping

The mobile adapter maps contributor roles from the role-specific fields on AudibleApi's library `Item` DTO.

| Mobile field | AudibleApi DTO source | Rule |
|---|---|---|
| Author | `Item.Authors[].Name` | Join nonblank names in source order |
| Narrator | `Item.Narrators[].Name` | Join nonblank names in source order |
| Publisher | `Item.PublisherName` / `Item.Publisher` | Not a contributor; never copied into author or narrator |

In the generated AudibleApi library DTO, `authors` and `narrators` are separate nullable `Person[]` properties. Each `Person` has `Asin` and `Name`; the containing array supplies the role. There is no role property on `Person`. Consequently, the adapter does not infer roles from generic metadata, publisher fields, editorial summaries, or relationship records. Unknown or absent contributor roles remain empty.

The mapper trims names, ignores null/empty/whitespace-only names, preserves the remaining source order, and joins names with a comma and space. Missing arrays map to empty strings under the current `RemoteAudiobook` contract.

## Upstream references

- [AudibleApi library DTO](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi.Common/LibraryDtoV10.generated.cs) — generated `Item.Authors`, `Item.Narrators`, `Item.PublisherName`, and `Person.Name` properties.
- [AudibleApi Item helpers](https://github.com/rmcrackan/AudibleApi/blob/master/AudibleApi.Common/Item.cs) — `ProductId`, `LengthInMinutes`, `TitleWithSubtitle`, and `Publisher` aliases.

The adapter references the pinned NuGet package version `AudibleApi 14.1.2`. The repository's CI restore/build validates the API surface against that package. The upstream links above document the DTO shape; the tests exercise actual `Item` and `Person` instances from the referenced package.
