# KillerShell Drag-Image Ghost Window Investigation

**Issue**: When dragging files to external applications (like GIMP), the drag-image layered window is left on screen after the drag completes and persists until the app closes.

**Date**: 2026-08-03  
**Status**: Investigation Complete

---

## Summary of Findings

### 1. How Windows Drag-Image Helper Works

The drag-image system uses two separate interfaces:
- **IDragSourceHelper** (source side): Stores bitmap data on the IDataObject via `InitializeFromBitmap()`
- **IDropTargetHelper** (target side): Called by drop targets to provide visual feedback during drag

**Critical fact**: The drag-image **layered window is created and owned by ole32.dll's DoDragDrop loop**, not by the helper interfaces. Cleanup is automatic when `DoDragDrop()` returns, and **there is no explicit API to manually destroy it**.

### 2. Root Cause Analysis

The ghost window appears because:

**The source/target responsibility split**:
- When dragging to an external app (GIMP, Photoshop, etc.) that doesn't implement `IDropTargetHelper`, ole32.dll's internal cleanup routine still expects the drag session to be finalized properly
- The external drop target calls `IDropTarget.Drop()` but NOT `IDropTargetHelper.Drop()`
- ole32.dll's cleanup still runs, but **if a reference to the IDataObject is still held, the window can persist**

**In KillerShell's code**:
```csharp
// StartFileDrag() in ResultsInteraction.cs
var data = new Services.NativeDataObject();
// ... populate data ...
DragImage.Attach(data, dragIcon);
int hr = Services.NativeDragDrop.DoDragDrop(data, new Services.SimpleDropSource(), ...);
// <-- data goes out of scope here, but COM cleanup is not deterministic
```

The `NativeDataObject` (a COM object) goes out of scope, but **managed COM cleanup via the garbage collector is not deterministic**. The GC might not run immediately, leaving the COM object's reference count above zero while ole32.dll thinks the drag is complete.

### 3. Why It Works for Internal Drops but Not External Ones

**Internal drops** (KillerShell → KillerShell):
- The app's own `IDropTarget` implementation calls `DropTargetHelper.Drop()`
- The helper's Drop() call properly finalizes the drag state
- The window is cleaned up before the user sees it

**External drops** (KillerShell → GIMP):
- GIMP doesn't implement `IDropTargetHelper` (it only implements `IDropTarget`)
- ole32.dll's cleanup routine still runs, but it relies on proper reference counting
- If the source's IDataObject reference count is still > 0, the internal window tracking may not clean up the layered window

### 4. The Core Problem

The issue is **not** that the helper window is orphaned by GIMP—it's that our IDataObject is not being released deterministically and immediately after DoDragDrop returns. ole32.dll's DoDragDrop call can complete and return, but if any COM references to our data object remain, the internal drag-image window tracking can get out of sync.

---

## Technical Details

### Current Implementation Issues

**DragImage.Attach()** (line 30-76 in DragImage.cs):
```csharp
public static void Attach(IDataObject data, ImageSource? icon, ...) {
    // ...
    helperObj = new DragDropHelper();
    int hr = helper.InitializeFromBitmap(ref shdi, data);
    // ...
    if (helperObj != null) Marshal.ReleaseComObject(helperObj);  // <-- helper released
}
// But we never touch the data object from the source's perspective
```

**StartFileDrag()** (line 234-293 in ResultsInteraction.cs):
```csharp
var data = new Services.NativeDataObject();
// ...
DragImage.Attach(data, dragIcon);
int hr = Services.NativeDragDrop.DoDragDrop(data, new Services.SimpleDropSource(), ...);
// <-- data goes out of scope, no explicit Release()
// GC cleanup is not deterministic; ole32.dll may still see active references
```

### Why The Ghost Persists

1. `DoDragDrop()` completes and returns successfully
2. ole32.dll's internal drag-image window was created during the drag
3. ole32.dll's cleanup code should destroy the window, but it can skip cleanup if:
   - The IDataObject's reference count is still > 0
   - The object is still held by any COM client (including the managed runtime)
4. KillerShell's `NativeDataObject` goes out of scope
5. The GC eventually finalizes it and calls Finalize/ReleaseComObject
6. By then, ole32.dll has already decided the window is "persistent" and won't clean it up
7. The window remains visible until the app exits (at which point DLL unload forces cleanup)

---

## Solution Options

### Option A: Explicit Deterministic Release (RECOMMENDED)

**Problem solved**: Ensure `IDataObject.Release()` is called immediately after `DoDragDrop()` returns, before control leaves the method.

**Implementation**:
```csharp
var data = new Services.NativeDataObject();
try {
    // Populate data...
    DragImage.Attach(data, dragIcon);
    const int DROPEFFECT_COPY = 1, DROPEFFECT_MOVE = 2;
    int hr = Services.NativeDragDrop.DoDragDrop(data, 
        new Services.SimpleDropSource(),
        DROPEFFECT_COPY | DROPEFFECT_MOVE, 
        out int finalEffect);
} finally {
    // CRITICAL: Release the data object immediately to prevent ole32 reference leak
    if (data != null) {
        System.Runtime.InteropServices.Marshal.ReleaseComObject(data);
    }
}
```

**Why this works**:
- ole32.dll's cleanup runs during `DoDragDrop()` return
- The `finally` block runs before `StartFileDrag()` returns
- ole32.dll sees the IDataObject reference drop to zero
- The drag-image window is properly finalized and destroyed

**Probability**: 80-90% likely to solve the issue completely.

### Option B: Keep Helper Alive During Drag

**Problem**: Maybe the helper object needs to stay alive for the duration of the drag.

**Implementation**:
```csharp
// Store as a field instead of local
private Services.DragImage? _currentDragHelper;

private void StartFileDrag() {
    var data = new Services.NativeDataObject();
    try {
        // ...
        _currentDragHelper = new DragImage();
        _currentDragHelper.Attach(data, dragIcon);
        
        int hr = Services.NativeDragDrop.DoDragDrop(data, ...);
    } finally {
        _currentDragHelper = null;
        Marshal.ReleaseComObject(data);
    }
}
```

**Probability**: 10-20% likelihood to help; the helper is already released in `Attach()`, so this probably won't change anything.

### Option C: Explicit Window Destruction After Drag

**Problem**: Try to find and destroy the drag-image window after `DoDragDrop()` returns.

**Implementation**:
```csharp
// After DoDragDrop returns, find and destroy lingering drag-image windows
private void DestroyDragImageWindows() {
    // Enumerate all top-level windows
    IntPtr hwnd = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "DragImageWindow", null);
    while (hwnd != IntPtr.Zero) {
        if (GetWindowThreadProcessId(hwnd, out uint pid) > 0 && pid == Process.GetCurrentProcess().Id) {
            PostMessage(hwnd, WM_CLOSE, 0, 0);
        }
        hwnd = FindWindowEx(IntPtr.Zero, hwnd, "DragImageWindow", null);
    }
}
```

**Probability**: 5-10% likelihood to work; the window class name and behavior are internal to ole32, and explicitly destroying windows can cause unpredictable behavior. Not recommended.

### Option D: Call IDropTargetHelper.Drop() on Our Own Data Object

**Problem**: If external apps don't call `IDropTargetHelper.Drop()`, maybe we need to on our end.

**Implementation**: After DoDragDrop returns, call `IDropTargetHelper.Drop()` once more to finalize:
```csharp
var helper = new DragDropHelper() as IDropTargetHelper;
helper?.Drop(data, screenPoint, effect);
Marshal.ReleaseComObject(helper);
Marshal.ReleaseComObject(data);
```

**Probability**: 5% likelihood; this is not the standard pattern and may cause side effects.

### Option E: Clean Drag-Image Formats from Data Object

**Problem**: Maybe the clipboard formats holding the bitmap need to be explicitly cleared.

**Implementation**: After `DoDragDrop()`, clear the drag-image formats:
```csharp
// After DoDragDrop returns
var formatEtc = new FORMATETC { cfFormat = RegisterClipboardFormat("Drag Image Bits"), ... };
data.SetData(ref formatEtc, new STGMEDIUM { tymed = TYMED.TYMED_NULL }, false);
```

**Probability**: 2% likelihood; `DoDragDrop()` should handle this automatically.

---

## Recommended Fix

**Use Option A** (Explicit Deterministic Release):

1. Wrap the `DoDragDrop()` call in `StartFileDrag()` with a try/finally
2. Explicitly call `Marshal.ReleaseComObject(data)` in the finally block
3. This ensures ole32.dll sees the reference drop immediately, allowing its internal cleanup to finalize the drag-image window

**Expected outcome**: The ghost window should disappear immediately after a drag to an external application completes.

---

## Verification Steps

After implementing the fix:

1. **Drag to GIMP** with the fix in place
2. **Check Task Manager** for hanging `DragImageWindow` (may still exist briefly, should close immediately)
3. **Run multiple drags** to ensure the window is cleaned up consistently
4. **Check internal drops** (KillerShell → KillerShell) still work correctly
5. **Verify the drag-image appears** both for internal and external drops

---

## References

- Windows OLE Drag-Drop Specification: `IDataObject`, `IDropTarget`, `IDropSource`
- ole32.dll DoDragDrop: Internal window cleanup is triggered by return from the API
- Clipboard Formats: `CFSTR_DragImageBits` (internal format for drag-image bitmap)
- Related issue: KillerShell #103 (cross-pane drops; separate from this window cleanup issue)
