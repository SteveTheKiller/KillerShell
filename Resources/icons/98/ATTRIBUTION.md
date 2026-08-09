# 98SE icon pack

The period icon set the **98SE** theme draws from. Every file here is a Windows-95-era
counterpart to the same-named file one directory up in the brand pack, so `IconCache` swaps
between the two by changing a path prefix and nothing else. Keep the two directories in sync:
adding a brand icon without adding its 98 counterpart is safe (the flat theme falls back to the
brand art) but it will look out of place.

## Source and licence

Art from **Chicago95**, licensed **GPL-3.0+/MIT**.

- Upstream: <https://github.com/grassmunk/Chicago95>
- Files taken from: `Icons/Chicago95/` (the plain theme, not `-tux` or `-puffy`, which swap in
  the Linux and OpenBSD mascots)

GPL-3.0+ is compatible with KillerShell's own GPLv3, so this ships with the app under the same
terms. This is why the set is Chicago95 rather than the actual Windows 98 icons: those are
Microsoft's, and a GPLv3 work has to be distributable in full under GPLv3.

## Processing

Each icon is the **32px** original, upscaled **5x with nearest-neighbour** to the 160px canvas
the brand pack uses. Integer scale, no interpolation - anything else turns hard pixel edges into
mush, and the whole point of the set is that the pixels stay pixels. Nothing was recoloured or
redrawn.

Regenerate rather than hand-editing anything in this folder.

## Source map

| Icon | Chicago95 source |
|---|---|
| `folder_icon` | `places/32/folder.png` |
| `drive_icon` | `devices/32/drive-harddisk.png` |
| `my_pc_icon` | `devices/32/computer.png` |
| `home_folder_icon` | `places/32/user-home.png` |
| `desktop_folder_icon` | `places/32/user-desktop.png` |
| `documents_folder_icon` | `places/32/folder-documents.png` |
| `pictures_folder_icon` | `places/32/folder-pictures.png` |
| `music_folder_icon` | `places/32/folder-music.png` |
| `videos_folder_icon` | `places/32/folder-videos.png` |
| `downloads_folder_icon` | `places/32/folder-download.png` |
| `favorites_folder_icon` | `places/32/user-bookmarks.png` |
| `program_files_icon` | `categories/32/applications-other.png` |
| `windows_folder_icon` | `places/32/distributor-logo.png` |
| `recents_icon` | `places/32/folder-recent.png` |
| `search_results_icon` | `places/32/folder-saved-search.png` |
| `term_icon` | `apps/32/utilities-terminal.png` |
| `admin_term_icon` | `apps/32/gksu-root-terminal.png` |
| `dead_shell_icon` | `apps/32/utilities-terminal.png` + `actions/32/process-stop.png` badge |
| `text_document_icon` | `mimes/32/text-x-generic.png` |
| `text_editor_icon` | `apps/32/accessories-text-editor.png` |
| `event_viewer` | `apps/32/logviewer.png` |
| `task_manager` | `apps/32/utilities-system-monitor.png` |
| `registry_editor_icon` | `apps/32/dconf-editor.png` |
| `perf_icon` | `apps/32/xfce4-cpugraph-plugin.png` |

Notes on the ones that are not a straight name match:

- **`dead_shell_icon`** is the only composite: the terminal with the stop badge alpha-composited
  into its bottom-right at 18px, before the upscale. Chicago95 has no exited-shell icon and the
  brand pack's own dead shell is likewise a terminal variant.
- **`perf_icon`** is the CPU chip, not the monitor-with-a-graph. `utilities-system-monitor` is
  already `task_manager`, and the two sat side by side looking identical. The chart mime icon was
  tried first and reads as Excel (Steve, 2026-08-08).
- **`windows_folder_icon`** is the four-colour flag, which is what the Windows directory ought to
  wear.
- **`registry_editor_icon`** is dconf-editor, the Linux set's registry editor. There is no regedit.
- **`documents_folder_icon`** is the briefcase, which is what Chicago95 ships as its documents
  folder. Period-correct as My Briefcase; a shade odd as My Documents.
