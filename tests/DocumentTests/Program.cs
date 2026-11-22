var root = Path.Combine(Path.GetTempPath(), "SpektraDocumentTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var a = Path.Combine(root, "A", "A.cocatalogdb");
var b = Path.Combine(root, "B", "B.cosessiondb");
foreach (var p in new[] {a,b}) { Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p,"SQLite format 3\0"); }
Environment.SetEnvironmentVariable("SPEKTRAFILM_CATALOG", a);
Environment.SetEnvironmentVariable("SPEKTRAFILM_DOCUMENT_MODE", args.Contains("single") ? "single" : "all");
Environment.SetEnvironmentVariable("SPEKTRAFILM_DOCUMENT_ROOT", root);
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
Check(ApprovedCatalog.IsApproved(a), "original catalog accepted");
Check(ApprovedCatalog.IsApproved(Path.GetDirectoryName(a)), "catalog bundle accepted");
Check(ApprovedCatalog.IsApproved(b) == !args.Contains("single"), "session after catalog respects document mode");
Check(!ApprovedCatalog.IsApproved(null), "no document is rejected");
Check(!ApprovedCatalog.IsApproved("relative.cosessiondb"), "relative path is rejected");
Check(!ApprovedCatalog.IsApproved(Path.Combine(root,"missing.cosessiondb")), "missing document is rejected");
var outside=Path.Combine(Path.GetTempPath(),"SpektraOutside-"+Guid.NewGuid()+".cosessiondb");
File.WriteAllText(outside,"SQLite format 3\0");
Check(!ApprovedCatalog.IsApproved(outside), "test mode never accepts an original outside test root");
File.Delete(outside);
Directory.Delete(root,true);
