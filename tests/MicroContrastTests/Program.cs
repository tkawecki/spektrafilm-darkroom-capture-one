using System.Text.Json;
var cases=JsonSerializer.Deserialize<Case[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures.json")),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
double maxError=0;int checks=0;
foreach(var c in cases){var output=(float[])c.Source.Clone();FinishingMicroContrast.Apply(output,c.Width,c.Height,c.Amount,CancellationToken.None);for(int i=0;i<output.Length;i++){double error=Math.Abs(output[i]-c.Expected[i]);maxError=Math.Max(maxError,error);if(error>2e-7)throw new Exception($"Reference mismatch {c.Width}x{c.Height}, amount{c.Amount}, i{i}: {error}");if(i%4==3&&output[i]!=c.Source[i])throw new Exception("Alpha changed");checks++;}}
var constant=Enumerable.Range(0,1024*512).SelectMany(_=>new[]{.4f,.6f,.2f,.75f}).ToArray();var original=(float[])constant.Clone();FinishingMicroContrast.Apply(constant,1024,512,1,CancellationToken.None);if(!constant.SequenceEqual(original))throw new Exception("Constant field changed on parallel path");
using var stop=new CancellationTokenSource();stop.Cancel();try{FinishingMicroContrast.Apply(original,1024,512,1,stop.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException){}
Console.WriteLine($"PASS {cases.Length} reference cases / {checks} channels; max error {maxError}; parallel constant field, alpha and cancellation");
record Case(int Width,int Height,double Amount,float[] Source,float[] Expected);
