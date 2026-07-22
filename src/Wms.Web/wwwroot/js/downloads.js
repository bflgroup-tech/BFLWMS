// Downloads a base64-encoded file, prompting the user for a save location
// when the browser supports the File System Access API (Chrome/Edge), and
// falling back to a normal browser download otherwise (Firefox/Safari, or
// if the user cancels for an unrelated reason won't retry).
window.downloadFileWithPicker = async function (fileName, base64Data, mimeType) {
    const byteChars = atob(base64Data);
    const bytes = new Uint8Array(byteChars.length);
    for (let i = 0; i < byteChars.length; i++) {
        bytes[i] = byteChars.charCodeAt(i);
    }

    if (window.showSaveFilePicker) {
        let handle;
        try {
            const ext = fileName.slice(fileName.lastIndexOf('.'));
            handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: [{
                    description: 'Excel Workbook',
                    accept: { [mimeType]: [ext] }
                }]
            });
        } catch (err) {
            if (err && err.name === 'AbortError') return; // user cancelled the save dialog
            console.error('downloadFileWithPicker: showSaveFilePicker failed, falling back to default download', err);
            handle = null; // picker itself failed before touching the target file; safe to fall through
        }

        if (handle) {
            try {
                // Write the raw bytes directly (not a Blob) — some Chromium builds have had
                // inconsistent Blob handling in FileSystemWritableFileStream.write(), whereas a
                // plain BufferSource is the simplest, most compatible path.
                const writable = await handle.createWritable();
                await writable.write(bytes);
                await writable.close();

                // Verify the write actually landed correctly before declaring success — some
                // environments (antivirus/EDR hooking file I/O, sync-client locks, etc.) can let
                // write()/close() resolve without throwing while still producing a truncated or
                // otherwise-wrong file on disk. Re-read what was actually saved and compare size.
                const savedFile = await handle.getFile();
                if (savedFile.size === bytes.length) return;
                console.error('downloadFileWithPicker: saved file size (' + savedFile.size +
                    ') does not match expected size (' + bytes.length + ') — falling back to the default downloads folder');
            } catch (err) {
                // The picker already created/reserved the target file — it may now hold a
                // partial write. Clean that up on a best-effort basis and fall back to a normal
                // browser download so the user always ends up with one working file, rather than
                // silently double-downloading (an earlier bug) or leaving the user with nothing.
                console.error('downloadFileWithPicker: write to chosen location failed, falling back to the default downloads folder', err);
            }
            try { await handle.remove(); } catch { /* best-effort cleanup; ignore */ }
        }
    }

    const blob = new Blob([bytes], { type: mimeType });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};
