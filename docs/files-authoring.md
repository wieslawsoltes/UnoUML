# Files tree authoring

UnoUML's Files inspector edits the semantic paths in an `@startfiles` page. The
native renderer and editor share the same bounded path index, including folders
inferred from their children. A folder can therefore be selected and renamed
even when no separate folder declaration exists in the source.

## Canvas and inspector

Select an icon-and-caption row to see its full path and applicable operations.
Double-click or press F2 to rename the final path component. Renaming a folder
updates every descendant declaration in one source transaction. A field uses an
exact UTF-16 name token, so implicit folders and their leaf names on the same
source line have separate hit targets.

The inspector offers the following operations:

| Operation | Result |
| --- | --- |
| Insert file or folder | Insert an absolute path at a chosen declaration position. Intermediate folders are inferred; a trailing slash declares a folder. |
| Rename | Change a basename while retaining its current parent. All descendant paths follow a renamed folder. |
| Move | Change the complete destination path of a file or subtree. Missing intermediate folders are inferred. |
| Duplicate | Copy a file or complete subtree to a new path, including safely mapped authored geometry. |
| Reorder | Change an entry's order among its siblings. Path tokens are reordered in existing declaration slots; comments and other trivia retain their slots. |
| Remove | Remove a file, or a folder and its descendants. An inline comment on a removed declaration remains as standalone trivia. |

The diagram inspector also has a picker for every file and explicit or implicit
folder, so structural operations remain reachable without selecting SVG geometry.
The source outline's path declarations expose the same operations.

Ctrl/Cmd+C copies a selected subtree as a self-contained absolute-path fragment.
For example, copying `/src/models/` containing `Order.cs` produces
`/models/Order.cs`. Ctrl/Cmd+V inserts that fragment into the selected folder,
the selected file's parent, or the root when no row is selected. A conflicting
root name gets ` copy`, ` copy 2`, and subsequent deterministic suffixes; file
extensions are retained. Ctrl/Cmd+X and Delete use the same validated removal
operation. The ordinary duplicate shortcut uses a suggested sibling copy path.

These operations change the diagram text only. They do not rename, create, or
delete files on the device or in an imported archive.

## Geometry and identity

Native Files identities derive from the complete parent identity, basename, and
file/directory kind. The same path has the same identity whether its directory
was declared explicitly or inferred. Unrelated insertion and reordering do not
change existing identities.

A structural rename or move supplies an explicit old-path/new-path map. Every
mapped canvas row retains its position, dimensions, rotation, pin, lock and
stacking order under the new identity. Metadata for rows that disappear is
removed. Source changes and metadata changes share one undo entry. Direct edits
in the Source view cannot infer the user's rename intent and do not apply this
map automatically.

Duplication and clipboard paste use the same canvas-copy projection as the other
structural editors. A copy starts two destination-grid cells diagonally from its
original; repeated copies advance that offset if an earlier copy occupies it.
Unrelated geometry is unchanged. Clipboard geometry uses the existing versioned
`' @unouml:canvas-selection` transport comment; it is validated, remapped to the
actual destination paths, and removed before inserting the fragment.

Files rows are independent geometry. Folder ancestry describes path semantics;
it does not invent enclosing folder rectangles or connectors. A native listing
with up to 2,048 rows exposes free-canvas metadata. Larger supported native
listings retain their file rendering and source-aware fields, up to the shared
4,096-row structural limit.

## Validation and source retention

The editor accepts complete literal path pages and understood style/skinparam
blocks. It rejects unknown statements before offering structural mutations.
Paths must be absolute, have no empty, `.` or `..` components, and respect the
128-component and 2,048-character limits. Duplicate explicit declarations,
file/folder collisions, moves into a folder's own descendants and implicit
merges with existing destinations are rejected. Names containing preprocessor
expressions or syntax outside the literal path model remain source edits.

A path may materialize an existing implicit directory with an explicit trailing
slash declaration. This preserves the directory's identity and geometry.

Locked rows, including locked descendants of a selected folder, cannot be
removed by structural operations. Semantic renaming retains the lock and the
visual position. Invalid existing canvas metadata and metadata capacity failures
leave both source and history unchanged.

The parser records offsets into the untouched page. Localized changes preserve
BOMs, CR/LF/CRLF line endings, other diagram pages, indentation and unrelated
comments. Every operation validates the entire resulting page before committing.
Snapshots are tied to the original source object and active page, so stale
dialogs and source fields cannot overwrite a later edit.

## C# API

All structural operations are in `UnoUML.Core`, independent of Uno and the browser:

```csharp
using UnoUML.Core;

var document = new StudioDocument("""
    @startfiles
    /src/Order.cs
    /src/models/LineItem.cs
    /tests/
    @endfiles
    """);

FilesEntryEdit models = document.FindFilesEntry("/src/models/")!;
if (!document.MoveFilesEntry(models, "/src/domain/"))
    throw new InvalidOperationException("The move did not pass validation.");

// Reopen immutable snapshots after every mutation.
string fragment = document.CopyFilesEntry(
    document.FindFilesEntry("/src/domain/")!)!;
if (!document.PasteFilesEntries(document.OpenFilesDiagram()!, fragment, "/tests/"))
    throw new InvalidOperationException("The paste did not pass validation.");

document.Undo(); // Restores the source and geometry before the paste.
```

The remaining entry points are `InsertFilesEntry`, `RenameFilesEntry`,
`DuplicateFilesEntry`, `ReorderFilesEntry`, `RemoveFilesEntry`,
`SuggestFilesCopyPath`, and `OpenFilesEntry` for renderer fields or outline
objects. `RemoveFilesEntry` requires `recursive: true` for a nonempty directory.

## Reference behavior and limits

PlantUML's [Files documentation](https://plantuml.com/files-diagram) specifies
source-order presentation and merging of common directory paths. The pinned
[Files implementation](https://github.com/plantuml/plantuml/tree/v1.2026.9/src/main/java/net/sourceforge/plantuml/filesdiagram)
also distinguishes folder prefixes from final file names. These contracts guide
the shared index and stable ordering; UnoUML does not sort file names implicitly.

Notes, extended Creole/HTML path captions, generated path programs and ambiguous
duplicate paths remain on the documented source or compatibility-renderer
routes. The structural editor refuses partially understood pages. Free geometry
does not establish general filesystem operations or folder-container layout.
