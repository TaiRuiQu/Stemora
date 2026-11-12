using static Stemora.Core.Strings;

namespace Stemora.Core;

public static class MuScriptorProgress
{
    public static string Label(string stage) => Tr(stage switch
    {
        "runtime-download" => "正在下载环境安装工具",
        "runtime-python" => "正在安装 Python 3.12",
        "runtime-packages" => "正在下载和安装 MuScriptor / PyTorch 依赖",
        "runtime-verify" => "正在检查 MuScriptor 运行环境",
        "runtime-ready" => "MuScriptor 运行环境已就绪",
        "runtime" => "正在准备 MuScriptor 运行环境",
        "download" => "正在下载 MuScriptor 模型",
        "verify" => "正在校验模型",
        "separate" => "正在提取人声",
        "loading" => "正在加载 MuScriptor",
        "transcribe" => "正在识别音符",
        "analysis-packages" => "正在安装节奏与调性分析依赖",
        "analysis-download" => "正在下载和校验节奏与调性模型",
        "analyze" => "正在分析节奏与调性",
        "analyze-beats" => "正在识别 BPM 与拍点",
        "analyze-key" => "正在识别调性",
        "spectrum" => "正在分析原人声频谱",
        _ => "正在准备下载"
    });
}
