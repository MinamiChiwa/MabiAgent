using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
namespace MabiAgent.Windows;

/// <summary>Offline PaddleOCR single-line CTC recognition via ONNX Runtime.</summary>
public sealed class OnnxTextRecognizer:IColorOcrEngine,IDisposable
{
    public const string DefaultModelFileName="ch_PP-OCRv5_rec_mobile.onnx";
    private InferenceSession session;private readonly string input;private readonly string[] characters;private readonly object gate=new();private bool disposed;
    public string ModelPath{get;}
    public bool LowCpu{get;private set;}
    public OnnxTextRecognizer(string modelPath,string? dictionaryPath=null,bool lowCpu=false)
    {
        ModelPath=Path.GetFullPath(modelPath);if(!File.Exists(ModelPath))throw new FileNotFoundException("缺少离线 OCR 识别模型",ModelPath);
        LowCpu=lowCpu;
        using var options=Options(lowCpu);
        session=new(ModelPath,options);
        try
        {
            if(session.InputMetadata.Count!=1)throw new InvalidDataException("需要单输入 OCR 模型");input=session.InputMetadata.Keys.Single();var shape=session.InputMetadata[input].Dimensions;if(shape.Length!=4||shape[1]!=3)throw new InvalidDataException("OCR 输入必须为 NCHW RGB");
            string[] labels;if(session.ModelMetadata.CustomMetadataMap.TryGetValue("character",out var dictionary))labels=dictionary.Split([(char)13,(char)10],StringSplitOptions.RemoveEmptyEntries);else if(dictionaryPath is not null)labels=File.ReadAllLines(dictionaryPath);else throw new InvalidDataException("模型没有字典元数据，需配置原字典");
            characters=new[]{""}.Concat(labels).Append(" ").ToArray();
            var output=session.OutputMetadata.Values.Single().Dimensions;if(output.Length!=3||output[2]>0&&output[2]!=characters.Length)throw new InvalidDataException("OCR 输出或字典长度不符");
        }catch{session.Dispose();throw;}
    }
    private static SessionOptions Options(bool lowCpu)
    {
        var options=new SessionOptions{IntraOpNumThreads=2,InterOpNumThreads=1,ExecutionMode=ExecutionMode.ORT_SEQUENTIAL};
        if(lowCpu){options.AddSessionConfigEntry("session.intra_op.allow_spinning","0");options.AddSessionConfigEntry("session.inter_op.allow_spinning","0");}
        return options;
    }
    // Call only at a workflow boundary. The lock also protects existing workbench
    // references: they retain this engine and cannot race session replacement.
    public void ConfigureLowCpu(bool lowCpu)
    {
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            if(LowCpu==lowCpu)return;
            using var options=Options(lowCpu);var replacement=new InferenceSession(ModelPath,options);
            try
            {
                if(replacement.InputMetadata.Count!=1||!replacement.InputMetadata.ContainsKey(input)
                    ||replacement.InputMetadata[input].Dimensions.Length!=4||replacement.InputMetadata[input].Dimensions[1]!=3
                    ||replacement.OutputMetadata.Count!=1||replacement.OutputMetadata.Values.Single().Dimensions.Length!=3
                    ||replacement.OutputMetadata.Values.Single().Dimensions[2]>0&&replacement.OutputMetadata.Values.Single().Dimensions[2]!=characters.Length)
                    throw new InvalidDataException("OCR 模型已变化，请重新启动工具");
            }catch{replacement.Dispose();throw;}
            var previous=session;session=replacement;LowCpu=lowCpu;previous.Dispose();
        }
    }
    public OcrLine? Recognize(int width,int height,byte[] grayPixels)
        =>RecognizePixels(width,height,grayPixels,1);
    public OcrLine? RecognizeRgb(RgbImage image)=>RecognizePixels(image.Width,image.Height,image.CopyPixels(),3);
    private OcrLine? RecognizePixels(int width,int height,byte[] pixels,int channels)
    {
        if(width<=0||height<=0||(long)width*height>1_048_576||pixels.Length!=(long)width*height*channels)throw new ArgumentException("OCR 需要有界原生单行裁图");
        var resizedWidth=(int)Math.Ceiling(48d*width/height);if(resizedWidth is <1 or >8192)throw new ArgumentException("OCR 行宽超过上限");var modelWidth=Math.Max(320,resizedWidth);
        using var original=new Mat(height,width,channels==1?MatType.CV_8UC1:MatType.CV_8UC3);System.Runtime.InteropServices.Marshal.Copy(pixels,0,original.Data,pixels.Length);using var resized=new Mat();Cv2.Resize(original,resized,new Size(resizedWidth,48),0,0,InterpolationFlags.Linear);var normalized=new float[3*48*modelWidth];var resizedPixels=new byte[48*resizedWidth*channels];System.Runtime.InteropServices.Marshal.Copy(resized.Data,resizedPixels,0,resizedPixels.Length);
        for(var c=0;c<3;c++)for(var y=0;y<48;y++)for(var x=0;x<resizedWidth;x++)normalized[c*48*modelWidth+y*modelWidth+x]=(resizedPixels[(y*resizedWidth+x)*channels+(channels==1?0:c)]/255f-.5f)/.5f;
        lock(gate)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            using var run=new RunOptions();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));using var cancel=timeout.Token.Register(()=>run.Terminate=true);
            var tensor=new DenseTensor<float>(normalized,[1,3,48,modelWidth]);Microsoft.ML.OnnxRuntime.IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results;
            try{results=session.Run([NamedOnnxValue.CreateFromTensor(input,tensor)],session.OutputNames,run);}catch(OnnxRuntimeException e)when(timeout.IsCancellationRequested){throw new TimeoutException("OCR 识别超过 5 秒",e);}
            using var result=results;var output=result.First().AsTensor<float>();if(output.Rank!=3||output.Dimensions[0]!=1||output.Dimensions[2]!=characters.Length)throw new InvalidDataException("OCR 输出或字典长度不符");
            var length=output.Dimensions[1];var indexes=new int[length];var probabilities=new float[length];
            for(var t=0;t<length;t++){float max=float.NegativeInfinity;var index=0;for(var c=0;c<characters.Length;c++){var value=output[0,t,c];if(value>max){max=value;index=c;}}indexes[t]=index;probabilities[t]=max;}return Decode(indexes,probabilities,characters);
        }
    }
    public static OcrLine? Decode(IReadOnlyList<int> indexes,IReadOnlyList<float> probabilities,IReadOnlyList<string> characters)
    {
        if(indexes.Count!=probabilities.Count)throw new ArgumentException("CTC 长度不一致");var text=new System.Text.StringBuilder();double sum=0;var count=0;
        for(var i=0;i<indexes.Count;i++){var index=indexes[i];if(index==0||i>0&&indexes[i-1]==index)continue;if(index<0||index>=characters.Count||!float.IsFinite(probabilities[i])||probabilities[i] is <0 or >1)return null;text.Append(characters[index]);sum+=probabilities[i];count++;}
        return count==0?null:new(text.ToString(),sum/count);
    }
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;session.Dispose();}}
}
