﻿using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Timers;
using System.Web.Script.Serialization;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Logging;

namespace HSPI_IdleLightColors {
	public class AnalyticsClient {
		private const string REPORT_URL = "https://hsstats.doctormckay.com/report.php";
		private const string GLOBAL_INI_FILENAME = "DrMcKayGlobal.ini";

		public string CustomSystemId {
			get {
				string customSystemId = _hs.GetINISetting("System", "ID", "", GLOBAL_INI_FILENAME);
				if (customSystemId.Length == 0) {
					customSystemId = Guid.NewGuid().ToString();
					_hs.SaveINISetting("System", "ID", customSystemId, GLOBAL_INI_FILENAME);
				}

				return customSystemId;
			}
		}

		private readonly HSPI _plugin;
		private readonly IHsController _hs;

		public AnalyticsClient(HSPI plugin, IHsController hs) {
			_plugin = plugin;
			_hs = hs;
		}

		public void ReportIn(int milliseconds) {
			Timer timer = new Timer(milliseconds) {Enabled = true, AutoReset = false};
			timer.Elapsed += (src, arg) => {
				Report();
			};
		}

		public async void Report() {
			try {
				ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
				
				JavaScriptSerializer json = new JavaScriptSerializer();
				HttpClient client = new HttpClient();
				HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, REPORT_URL) {
					Content = new StringContent(json.Serialize(_gatherData()), Encoding.UTF8, "application/json")
				};
				HttpResponseMessage res = await client.SendAsync(req);
				_plugin.WriteLog(ELogType.Trace, $"Analytics report: {res.StatusCode}");
				
				req.Dispose();
				res.Dispose();
				client.Dispose();
			} catch (Exception ex) {
				string errMsg = ex.Message;
				Exception inner = ex;
				while ((inner = inner.InnerException) != null) {
					errMsg += $" [{inner.Message}]";
				}
				
				_plugin.WriteLog(ELogType.Trace, $"Analytics report: {errMsg}");
			}
		}

		private string _getMonoVersion() {
			Type type = Type.GetType("Mono.Runtime");
			if (type != null) {
				MethodInfo displayName = type.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
				if (displayName != null) {
					return (string) displayName.Invoke(null, null);
				}
			}

			return "";
		}
		
		private AnalyticsData _gatherData() {
			return new AnalyticsData {
				CustomSystemId = this.CustomSystemId,
				PluginId = _plugin.Id,
				PluginVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString(),
				SystemEnvironmentVersion = Environment.Version.ToString(),
				SystemOsVersion = Environment.OSVersion.ToString(),
				MonoVersion = _getMonoVersion(),
				HsVersion = _hs.Version(),
				HsAppPath = _hs.GetAppPath(),
				HsOsType = _hs.GetOsType(),
				HsEdition = (int) _hs.GetHSEdition()
			};
		}
	}

	internal struct AnalyticsData {
		public string CustomSystemId;
		public string PluginId;
		public string PluginVersion;
		public string SystemEnvironmentVersion;
		public string SystemOsVersion;
		public string MonoVersion;
		public string HsVersion;
		public string HsAppPath;
		public int HsOsType;
		public int HsEdition;
	}
}
