using System.Globalization;

namespace Companion.Core;

public static partial class CaptureReader
{
	private static void ReadDiagnosticExclusions(Capture capture)
	{
		bool num = ReadExclusionSet(capture, "SupportsParticleCensus", "particle-census-exclusions.csv", 3, "Particle census", null);
		bool flag = ReadCryptExclusions(capture);
		if (!num && !flag)
		{
			return;
		}
		foreach (Frame frame in capture.Frames)
		{
			frame.DiagnosticExcluded = capture.IsDiagnosticExcluded(frame.Seconds - frame.Milliseconds / 1000.0, frame.Seconds);
		}
		if (!capture.DiagnosticExclusionsInvalid)
		{
			int value = capture.Frames.Count((Frame f) => f.DiagnosticExcluded);
			int value2 = capture.Methods.Count((MethodWindow m) => capture.IsDiagnosticExcluded(m.Start, m.End));
			int value3 = capture.CpuSamples.Count((CpuObservation s) => capture.IsDiagnosticExcluded(s.Seconds, s.Seconds));
			string value4 = (flag ? "Diagnostic censuses" : "Particle census");
			capture.Warnings.Add($"{value4}: {capture.DiagnosticExclusions.Count} guarded exclusion intervals; {value} frames, {value2} complete method windows and {value3} CPU samples excluded from timing analysis. Raw files and observations are retained; census counts do not allocate particle CPU time.");
			if (flag)
			{
				capture.Metadata["DiagnosticExcludedFrameIntervals"] = value.ToString(CultureInfo.InvariantCulture);
				capture.Metadata["DiagnosticExcludedCompleteMethodWindows"] = value2.ToString(CultureInfo.InvariantCulture);
				capture.Metadata["DiagnosticExcludedCpuSamples"] = value3.ToString(CultureInfo.InvariantCulture);
				capture.Warnings.Add("Crypt selection/arm/snapshot guards exclude complete frame intervals and method windows. Native loading work sharing those intervals is excluded too; the diagnostic and native work cannot be separated within them. Raw evidence remains available, but not every loading frame remains admitted.");
			}
		}
	}

	private static bool ReadCryptExclusions(Capture capture)
	{
		if (!capture.Metadata.TryGetValue("CryptSnapshotMaskVersion", out string? value))
		{
			return ReadExclusionSet(capture, "SupportsCryptSnapshot", "crypt-snapshot-exclusions.csv", 2, "Crypt snapshot", new string[2] { "arm", "snapshot" });
		}
		if (value == "2")
		{
			return ReadExclusionSet(capture, "SupportsCryptSnapshot", "crypt-snapshot-exclusions.csv", 7, "Crypt snapshot", new string[7] { "select-1", "select-2", "select-3", "arm-1", "arm-2", "arm-3", "snapshot" });
		}
		capture.DiagnosticExclusionsInvalid = true;
		capture.Warnings.Add("Crypt snapshot exclusion validation failed; all timing summaries and diagnostic timing associations are excluded (raw evidence retained): Invalid or unsupported CryptSnapshotMaskVersion.");
		return true;
	}

	private static bool ReadExclusionSet(Capture capture, string capabilityName, string fileName, int maximumRows, string label, string[]? admittedIds)
	{
		if (!capture.Metadata.TryGetValue(capabilityName, out string? value))
		{
			return false;
		}
		if (bool.TryParse(value, out var result) && !result)
		{
			return false;
		}
		try
		{
			if (!result)
			{
				throw new InvalidDataException("Invalid " + capabilityName + " capability.");
			}
			string text = Path.Combine(capture.Directory, fileName);
			if (!File.Exists(text))
			{
				throw new InvalidDataException("Advertised " + label + " exclusion file is missing.");
			}
			if (new FileInfo(text).Length > 65536)
			{
				throw new InvalidDataException("Diagnostic exclusion file exceeds 64 KB.");
			}
			using (StreamReader streamReader = new StreamReader(text))
			{
				if (streamReader.ReadLine() != "SnapshotId,StartSeconds,EndSeconds,StartFrame,EndFrame,Reason")
				{
					throw new InvalidDataException("Diagnostic exclusion header does not match its declared schema.");
				}
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			List<DiagnosticExclusion> list = new List<DiagnosticExclusion>();
			foreach (Dictionary<string, string> item in Csv.Read(text, maximumRows))
			{
				string text2 = Csv.Text(item, "SnapshotId");
				string text3 = Csv.Text(item, "Reason");
				if (!string.IsNullOrWhiteSpace(text2) && hashSet.Add(text2) && (admittedIds == null || admittedIds.Contains<string>(text2, StringComparer.Ordinal)) && !string.IsNullOrWhiteSpace(text3))
				{
					double? num = Csv.Number(item, "StartSeconds");
					if (num.HasValue)
					{
						double valueOrDefault = num.GetValueOrDefault();
						num = Csv.Number(item, "EndSeconds");
						if (num.HasValue)
						{
							double valueOrDefault2 = num.GetValueOrDefault();
							if (!(valueOrDefault < 0.0) && !(valueOrDefault2 < valueOrDefault) && int.TryParse(Csv.Text(item, "StartFrame"), NumberStyles.None, CultureInfo.InvariantCulture, out var result2) && result2 >= 0 && int.TryParse(Csv.Text(item, "EndFrame"), NumberStyles.None, CultureInfo.InvariantCulture, out var result3) && result3 >= result2)
							{
								list.Add(new DiagnosticExclusion(text2, valueOrDefault, valueOrDefault2, result2, result3, text3));
								continue;
							}
						}
					}
				}
				throw new InvalidDataException("Invalid or duplicate diagnostic exclusion interval.");
			}
			capture.DiagnosticExclusions.AddRange(list);
		}
		catch (Exception ex) when (((ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) ? 1 : 0) != 0)
		{
			capture.DiagnosticExclusionsInvalid = true;
			capture.Warnings.Add(label + " exclusion validation failed; all timing summaries and diagnostic timing associations are excluded (raw evidence retained): " + ex.Message);
		}
		return true;
	}

}
