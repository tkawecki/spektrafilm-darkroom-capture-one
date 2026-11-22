var folder=Path.Combine(Path.GetFullPath(args[0]),Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
var file=Path.Combine(folder,"saved-look.json");
var metadata=Convert.ToBase64String(Enumerable.Range(0,4*1024*1024).Select(i=>(byte)(i*17)).ToArray());
var saved=new Dictionary<string,string>{{"large-value",metadata},{"curve","[[0,0],[0.123456789012345,0.42],[1,1]]"}};
PanelFiles.WritePreset(file,saved);
var restored=PanelFiles.ReadPreset<Dictionary<string,string>>(file);
if(restored.Count!=saved.Count || saved.Any(p=>restored[p.Key]!=p.Value))throw new Exception("Large imported preset lost metadata after restart.");
var original=File.ReadAllBytes(file);
try {PanelFiles.WritePreset(file,new string('x',9*1024*1024));throw new Exception("Oversized write accepted");}
catch(InvalidDataException) { }
if(!original.SequenceEqual(File.ReadAllBytes(file)))throw new Exception("Rejected write destroyed old settings");
var oversized=Path.Combine(folder,"oversized.json");File.WriteAllText(oversized,"\""+new string('x',9*1024*1024)+"\"");
try {PanelFiles.ReadPreset<string>(oversized);throw new Exception("Oversized read accepted");}catch(InvalidDataException){}
if(Directory.GetFiles(folder,".spektra-*").Length!=0)throw new Exception("Temporary file leaked");
Console.WriteLine("PASS: full 4 MiB original preset + metadata roundtrip; symmetric size limit; failed write preserves existing look");
