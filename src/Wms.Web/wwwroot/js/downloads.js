// Downloads a base64-encoded file, prompting the user for a save location
// when the browser supports the File System Access API (Chrome/Edge), and
// falling back to a normal browser download otherwise (Firefox/Safari, or
// if the user cancels for an unrelated reason won't retry).
window.downloadFileWithPicker = async function (fileName, base64Data, mimeType) {
    const byteChars = atob(base64Data);
    const byteNumbers = new Array(byteChars.length);
    for (let i = 0; i < byteChars.length; i++) {
        byteNumbers[i] = byteChars.charCodeAt(i);
    }
    const blob = new Blob([new Uint8Array(byteNumbers)], { type: mimeType });

    if (window.showSaveFilePicker) {
        try {
            const ext = fileName.slice(fileName.lastIndexOf('.'));
            const handle = await window.showSaveFilePicker({
                suggestedName: fileName,
                types: [{
                    description: 'Excel Workbook',
                    accept: { [mimeType]: [ext] }
                }]
            });
            const writable = await handle.createWritable();
            await writable.write(blob);
            await writable.close();
            return;
        } catch (err) {
            if (err && err.name === 'AbortError') return; // user cancelled the save dialog
            // any other failure (e.g. unsupported context) falls through to the default download
        }
    }

    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
};
