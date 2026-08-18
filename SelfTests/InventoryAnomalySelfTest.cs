using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DfoGmTool.ServerCore.Infrastructure;
using DfoGmTool.Services;

namespace DfoGmTool.SelfTests
{
    internal static class InventoryAnomalySelfTest
    {
        private static int _failures;

        internal static int Run()
        {
            Console.WriteLine("=== INVENTORY_ANOMALIES selftest ===");
            var root = Path.Combine(Path.GetTempPath(), "dfo-gm-anomaly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                CheckUnreadyAndEmptyLegalIds();
                CheckUnreadyPvfRejectsStatusAndClean(root);
            }
            catch (Exception ex)
            {
                _failures++;
                Console.Error.WriteLine("UNHANDLED: " + ex);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }

            Console.WriteLine(_failures == 0
                ? "InventoryAnomalySelfTest OK"
                : $"InventoryAnomalySelfTest FAIL: {_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void CheckUnreadyAndEmptyLegalIds()
        {
            Check("unready PVF is rejected",
                !GmService.TryAcceptLegalItemIds(false, new HashSet<int> { 1 }, out var readyError));
            Check("unready PVF error mentions 尚未就绪",
                (readyError ?? string.Empty).Contains("尚未就绪"));
            Check("empty legal IDs are rejected",
                !GmService.TryAcceptLegalItemIds(true, new HashSet<int>(), out var emptyError));
            Check("empty legal IDs error mentions 误删",
                (emptyError ?? string.Empty).Contains("误删"));
            Check("null legal IDs are rejected",
                !GmService.TryAcceptLegalItemIds(true, null, out _));
            Check("non-empty ready IDs are accepted",
                GmService.TryAcceptLegalItemIds(true, new HashSet<int> { 1 }, out _));
        }

        private static void CheckUnreadyPvfRejectsStatusAndClean(string root)
        {
            var db = Path.Combine(root, "item.db");
            var dummyPvf = Path.Combine(root, "dummy.pvf");
            File.WriteAllBytes(dummyPvf, new byte[] { 0 });
            var schema = Path.Combine(AppContext.BaseDirectory, "ServerCore", "Sqlite", "item_schema.sql");
            SqliteDatabaseBootstrap.Initialize(db, schema);
            if (!GmConfig.TryCreate(db, dummyPvf, out var config, out var error) || config == null)
            {
                Check("dummy source config", false, error);
                return;
            }

            var pvf = new PvfIndexService(dummyPvf);
            var gm = new GmService(config, pvf);
            var status = gm.GetInventoryAnomalyStatus(pvf);
            Check("status rejects unready PVF", !IsSuccess(status));
            Check("status unready error mentions 尚未就绪",
                (GetStringProperty(status, "error") ?? string.Empty).Contains("尚未就绪"));
            var clean = gm.CleanInventoryAnomalies(pvf);
            Check("clean rejects unready PVF", !IsSuccess(clean));
            Check("clean unready error mentions 尚未就绪",
                (GetStringProperty(clean, "error") ?? string.Empty).Contains("尚未就绪"));
        }

        private static bool IsSuccess(object result)
        {
            if (result == null)
                return false;
            var prop = result.GetType().GetProperty("success", BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return prop != null && Convert.ToBoolean(prop.GetValue(result));
        }

        private static string GetStringProperty(object value, string propertyName)
        {
            if (value == null) return null;
            var prop = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return prop?.GetValue(value)?.ToString();
        }

        private static void Check(string name, bool condition, string error = null)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + name);
                return;
            }

            _failures++;
            Console.Error.WriteLine("FAIL " + name + (string.IsNullOrWhiteSpace(error) ? string.Empty : ": " + error));
        }
    }
}
