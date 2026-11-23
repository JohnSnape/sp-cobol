// MSBuild task: in a project that builds DOS-style screens (ScreenMode Both or
// Console), a form may only use controls that have a DOS-screen equivalent.
// Anything else stops the build with an error that names the control and both
// ways out -- the DOS screens are never given up silently.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

public class CobolScreenCheck : Task
{
    [Required] public ITaskItem[] Files { get; set; }
    [Required] public string ScreenMode { get; set; }

    // controls that map onto SCREEN SECTION items
    static readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "Label", "TextBox", "CheckBox", "Button", "Panel",
    };

    // this.listBox1 = new System.Windows.Forms.ListBox();
    static readonly Regex Creation = new Regex(
        @"this\.(?<name>\w+)\s*=\s*new\s+(?:global::)?System\.Windows\.Forms\.(?<type>\w+)\s*\(");

    public override bool Execute()
    {
        foreach (var f in Files)
        {
            string path = f.GetMetadata("FullPath");
            if (!File.Exists(path)) continue;
            var lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var m = Creation.Match(lines[i]);
                if (!m.Success) continue;
                string type = m.Groups["type"].Value, name = m.Groups["name"].Value;
                if (Allowed.Contains(type)) continue;
                Log.LogError("COBOL", "COB2001", null, path, i + 1, 0, 0, 0,
                    "'{0}' is a {1}, which has no DOS-screen equivalent. This project also builds a " +
                    "DOS-style screen (ScreenMode = {2}). Either remove the {1}, or make this a Windows-only " +
                    "app by setting <ScreenMode>Forms</ScreenMode> in the project file -- that gives up the " +
                    "DOS-style screens. Controls that work on both: Label, TextBox, CheckBox, Button, Panel.",
                    name, type, ScreenMode);
            }
        }
        return !Log.HasLoggedErrors;
    }
}
