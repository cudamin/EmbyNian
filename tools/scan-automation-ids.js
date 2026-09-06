// One-off inventory: every interactive XAML element, whether it already carries an
// AutomationId, and whether it sits inside a DataTemplate (where a shared id would be wrong).
// Line numbers come from splitting the file itself — grep -n is unreliable on this machine.
const fs = require('fs');
const path = require('path');

const ROOT = 'src/EmbyNian.Shell';
// The set WUI2020 checks, plus TextBox / ListView / GridView from winui-code-review's checklist.
const TAGS = String.raw`Button|RepeatButton|ToggleButton|HyperlinkButton|DropDownButton|SplitButton|ToggleSplitButton|PasswordBox|NumberBox|AutoSuggestBox|ComboBox|CheckBox|RadioButton|ToggleSwitch|Slider|RatingControl|GridView|ListView|TextBox|NavigationViewItem|MenuBarItem|MenuFlyoutItem|CalendarDatePicker|DatePicker|TimePicker|ColorPicker`;

// UserControls that exist once in markup but many times on screen — one per card, one per
// episode row. An id written here repeats across every instance, which is the same ambiguity
// a DataTemplate produces, so they are out of scope for the same reason. Their existing
// x:Names are there because code-behind needs the field, not as automation handles.
const REPEATED = ['PosterCard.xaml', 'EpisodeRow.xaml'];

function walk(dir, out = []) {
    for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) {
            if (e.name === 'bin' || e.name === 'obj') continue;
            walk(p, out);
        } else if (e.name.endsWith('.xaml')) out.push(p);
    }
    return out;
}

const open = new RegExp('<(' + TAGS + ')(?=[\\s/>])', 'g');
const rows = [];

for (const file of walk(ROOT)) {
    const text = fs.readFileSync(file, 'utf8');
    const lines = text.split(/\r?\n/);
    // Offset -> line number
    const lineAt = (off) => text.slice(0, off).split(/\r?\n/).length;

    let m;
    while ((m = open.exec(text)) !== null) {
        const tag = m[1];
        // Grab the element's own attribute span: from the tag to the first > that closes it.
        let i = m.index, depth = 0, end = -1;
        for (let j = m.index; j < text.length; j++) {
            const c = text[j];
            if (c === '"') { // skip quoted values
                j = text.indexOf('"', j + 1);
                if (j < 0) break;
                continue;
            }
            if (c === '>') { end = j; break; }
        }
        if (end < 0) continue;
        const span = text.slice(m.index, end + 1);
        const uid = (span.match(/x:Uid="([^"]+)"/) || [])[1] || '';
        const name = (span.match(/x:Name="([^"]+)"/) || [])[1] || '';
        const hasId = /AutomationProperties\.AutomationId/.test(span);
        // In a DataTemplate? Count unclosed <DataTemplate before this point.
        const before = text.slice(0, m.index);
        const inTemplate =
            (before.match(/<DataTemplate[\s>]/g) || []).length >
            (before.match(/<\/DataTemplate>/g) || []).length;
        const inStyle =
            (before.match(/<Style[\s>]/g) || []).length >
            (before.match(/<\/Style>/g) || []).length;
        rows.push({ file, line: lineAt(m.index), tag, uid, name, hasId, inTemplate, inStyle });
    }
}

// Measured 2026-09-06 with `winapp ui inspect`: WinUI reports x:Name as the UIA
// AutomationId when no explicit one is set, so an element with x:Name is already
// addressable. x:Uid is NOT a handle — it only feeds the resource loader.
const outside = rows.filter(
    (r) => !r.inTemplate && !r.inStyle && !REPEATED.some((f) => r.file.endsWith(f))
);
const handled = outside.filter((r) => r.hasId || r.name);
const need = outside.filter((r) => !r.hasId && !r.name);

console.log('total interactive elements   : ' + rows.length);
console.log('  inside a DataTemplate      : ' + rows.filter((r) => r.inTemplate).length + '  (a shared id would be ambiguous)');
console.log('  inside a Style/template    : ' + rows.filter((r) => r.inStyle).length);
console.log('  in a repeated UserControl  : ' + rows.filter((r) => REPEATED.some((f) => r.file.endsWith(f))).length + '  (' + REPEATED.join(', ') + ')');
console.log('  addressable already        : ' + handled.length + '  (explicit id ' + outside.filter((r) => r.hasId).length + ', via x:Name ' + outside.filter((r) => !r.hasId && r.name).length + ')');
console.log('  no handle at all           : ' + need.length);
console.log('');
let last = '';
for (const r of need) {
    if (r.file !== last) { console.log('--- ' + r.file); last = r.file; }
    console.log('  ' + String(r.line).padStart(4) + '  ' + r.tag.padEnd(18) + (r.uid ? 'Uid=' + r.uid : '(nothing)'));
}
console.log('');
console.log('=== skipped: in DataTemplate ===');
for (const r of rows.filter((x) => x.inTemplate))
    console.log('  ' + r.file + ':' + r.line + '  ' + r.tag);
