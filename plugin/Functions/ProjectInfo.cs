using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RhinoMCPPlugin.Functions;

/// <summary>
/// v3: the project info, entered once and read by every output: the title
/// blocks (and so the DWG/DXF), the PDF's metadata, the takeoff CSV's header
/// and the IFC project, site and building. One key per field in the
/// document strings, section forsk. The one source of the key list and the
/// captions. Pure, so it tests headless.
/// </summary>
public static class ProjectInfo
{
    public const string Section = "forsk";
    /// <summary>Set once the window asked for the info at a Print or Export, answered or not.</summary>
    public const string AskedKey = "info_asked";
    /// <summary>The plug-in settings key the architect prefills from: the firm is typed once per Mac.</summary>
    public const string ArchitectSetting = "forsk.architect";

    public const string Project = "project";
    public const string ProjectNo = "project_no";
    public const string Client = "client";
    public const string Address = "address";
    public const string Architect = "architect";
    public const string Date = "date";
    public const string Revision = "revision";

    /// <summary>The fields in the order the card, the CSV header and the contracts list them.</summary>
    public static readonly IReadOnlyList<string> Keys = new[] { Project, ProjectNo, Client, Address, Architect, Date, Revision };

    public sealed class Record
    {
        readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A field's trimmed value, or "" when unset.</summary>
        public string this[string key]
        {
            get => key != null && _values.TryGetValue(key, out var value) ? value : "";
            set
            {
                if (key == null) return;
                var text = value?.Trim() ?? "";
                if (text.Length == 0) _values.Remove(key);
                else _values[key] = text;
            }
        }
    }

    public sealed class Pdf
    {
        public string Title = "";
        public string Author = "";
        public string Subject = "";
        public string Keywords = "";
        public string Creator = "Forsk";
    }

    public sealed class Ifc
    {
        public string ProjectName = "";
        public string ProjectLongName = "";
        public string SiteName = "Site";
        /// <summary>The site's postal address line, or "" for none.</summary>
        public string Address = "";
        public string BuildingName = "";
        /// <summary>Forsk_ProjectInfo on the IfcBuilding: name and value, the empty ones left out.</summary>
        public List<KeyValuePair<string, string>> Properties = new List<KeyValuePair<string, string>>();
    }

    public static string Caption(string key, bool norwegian = false)
    {
        switch (key)
        {
            case Project: return SheetLang.Pick(norwegian, "Project", "Prosjekt");
            case ProjectNo: return SheetLang.Pick(norwegian, "Project no.", "Prosjektnr.");
            case Client: return SheetLang.Pick(norwegian, "Client", "Byggherre");
            case Address: return SheetLang.Pick(norwegian, "Address", "Adresse");
            case Architect: return SheetLang.Pick(norwegian, "Architect", "Arkitekt");
            case Date: return SheetLang.Pick(norwegian, "Date", "Dato");
            case Revision: return "Rev.";
            default: return key ?? "";
        }
    }

    /// <summary>Every field through get (a document string by key), trimmed.</summary>
    public static Record Read(Func<string, string> get)
    {
        var record = new Record();
        if (get == null) return record;
        foreach (var key in Keys)
            record[key] = get(key);
        return record;
    }

    /// <summary>The info counts as given once a project name is stored.</summary>
    public static bool Missing(Record record)
    {
        return string.IsNullOrWhiteSpace(record?[Project]);
    }

    /// <summary>The title block's date: a stored date wins, empty is the print day.</summary>
    public static string SheetDate(Record record, DateTime today)
    {
        var stored = record?[Date];
        return string.IsNullOrEmpty(stored) ? today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : stored;
    }

    /// <summary>
    /// The PDF's Info: Title "&lt;project&gt; — &lt;first&gt;–&lt;last&gt;" (or
    /// "&lt;project&gt; drawings"), Author the architect, Subject the address,
    /// Keywords "&lt;project_no&gt;; &lt;client&gt;; rev. &lt;revision&gt;" with
    /// empty parts left out, Creator Forsk.
    /// </summary>
    public static Pdf PdfInfo(Record record, string firstSheet = null, string lastSheet = null)
    {
        record = record ?? new Record();
        var project = record[Project];
        if (project.Length == 0) project = "Forsk";
        var first = firstSheet?.Trim() ?? "";
        var last = lastSheet?.Trim() ?? "";
        string title;
        if (first.Length == 0) title = project + " drawings";
        else if (last.Length == 0 || last == first) title = project + " — " + first;
        else title = project + " — " + first + "–" + last;
        var keywords = new List<string>();
        if (record[ProjectNo].Length > 0) keywords.Add(record[ProjectNo]);
        if (record[Client].Length > 0) keywords.Add(record[Client]);
        if (record[Revision].Length > 0) keywords.Add("rev. " + record[Revision]);
        return new Pdf
        {
            Title = title,
            Author = record[Architect],
            Subject = record[Address],
            Keywords = string.Join("; ", keywords)
        };
    }

    /// <summary>The CSV's header block: one English caption and value per field that is set, in key order.</summary>
    public static List<KeyValuePair<string, string>> CsvHeader(Record record)
    {
        var rows = new List<KeyValuePair<string, string>>();
        if (record == null) return rows;
        foreach (var key in Keys)
            if (record[key].Length > 0) rows.Add(new KeyValuePair<string, string>(Caption(key), record[key]));
        return rows;
    }

    /// <summary>
    /// The IFC's names: IfcProject Name the project number, else the project,
    /// LongName the project; IfcSite the address, else "Site"; IfcBuilding the
    /// project; Forsk_ProjectInfo on the building with Client, Architect,
    /// ProjectNumber, Revision and Date. fallbackName (the file's name) stands
    /// in for a project that is not set.
    /// </summary>
    public static Ifc IfcInfo(Record record, string fallbackName = null)
    {
        record = record ?? new Record();
        var project = record[Project];
        if (project.Length == 0) project = string.IsNullOrWhiteSpace(fallbackName) ? "Forsk" : fallbackName.Trim();
        var info = new Ifc
        {
            ProjectName = record[ProjectNo].Length > 0 ? record[ProjectNo] : project,
            ProjectLongName = project,
            SiteName = record[Address].Length > 0 ? record[Address] : "Site",
            Address = record[Address],
            BuildingName = project
        };
        void Add(string name, string key)
        {
            if (record[key].Length > 0) info.Properties.Add(new KeyValuePair<string, string>(name, record[key]));
        }
        Add("Client", Client);
        Add("Architect", Architect);
        Add("ProjectNumber", ProjectNo);
        Add("Revision", Revision);
        Add("Date", Date);
        return info;
    }
}
