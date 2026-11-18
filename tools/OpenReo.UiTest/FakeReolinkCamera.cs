using System.IO;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenReo.UiTest;

/// <summary>
/// Simulated Reolink camera HTTP API (cgi-bin/api.cgi) for automated tests.
/// Responses follow the documented formats: some sections in old firmware format (GetRec),
/// some in V20 format (FTP/E-Mail/Push), unsupported commands answer "not support".
/// All received Set commands are recorded.
/// </summary>
public sealed class FakeReolinkCamera : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (JsonObject Value, JsonObject? Range)> _data = new();
    public List<(string Cmd, JsonNode? Param)> SetCalls { get; } = new();
    public List<string> AllCommands { get; } = new();
    public int Port { get; }
    public int Logins, Logouts;

    /// <param name="legacy">true = older firmware (GetFtp/GetEmail/GetPush with mode digits), as reported by a real RLC camera.</param>
    public FakeReolinkCamera(bool legacy = false)
    {
        Port = 18000 + Random.Shared.Next(1000);
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        Seed();
        if (legacy) SeedLegacy();
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();
    private static string Sched(char c) => new(c, 168);

    private void Seed()
    {
        _data["GetDevInfo"] = (J("""{"DevInfo":{"model":"RLC-810A","type":"IPC","firmVer":"v3.1.0.2347_23061923","hardVer":"IPC_523128M8MP","serial":"00000000000000","channelNum":1,"buildDay":"build 23061923","detail":"IPC_523128M8MP...","name":"Einfahrt"}}"""), null);
        _data["GetDevName"] = (J("""{"DevName":{"name":"Einfahrt"}}"""), J("""{"DevName":{"name":{"maxLen":31}}}"""));
        _data["GetHddInfo"] = (J("""{"HddInfo":[{"capacity":121880,"format":1,"mount":1,"size":53211,"number":0}]}"""), null);
        _data["GetImage"] = (J("""{"Image":{"channel":0,"bright":128,"contrast":128,"saturation":128,"hue":128,"sharpen":128}}"""),
            J("""{"Image":{"bright":{"min":0,"max":255},"contrast":{"min":0,"max":255},"saturation":{"min":0,"max":255},"hue":{"min":0,"max":255},"sharpen":{"min":0,"max":255}}}"""));
        _data["GetIsp"] = (J("""{"Isp":{"channel":0,"antiFlicker":"Outdoor","backLight":"DynamicRangeControl","blc":128,"blueGain":128,"dayNight":"Auto","dayNightThreshold":50,"drc":128,"exposure":"Auto","gain":{"max":62,"min":1},"mirroring":0,"nr3d":1,"redGain":128,"rotation":0,"shutter":{"max":125,"min":0},"whiteBalance":"Auto"}}"""),
            J("""{"Isp":{"antiFlicker":["Outdoor","50HZ","60HZ","Off"],"backLight":["DynamicRangeControl","BackLightControl","Off"],"blc":{"min":0,"max":255},"blueGain":{"min":0,"max":255},"dayNight":["Auto","Color","Black&White"],"dayNightThreshold":{"min":0,"max":100},"drc":{"min":0,"max":255},"exposure":["Auto","LowNoise","Anti-Smearing","Manual"],"redGain":{"min":0,"max":255},"whiteBalance":["Auto","Manual"]}}"""));
        _data["GetIrLights"] = (J("""{"IrLights":{"channel":0,"state":"Auto"}}"""), null);
        _data["GetOsd"] = (J("""{"Osd":{"bgcolor":0,"channel":0,"osdChannel":{"enable":1,"name":"Einfahrt","pos":"Lower Right"},"osdTime":{"enable":1,"pos":"Top Center"},"watermark":1}}"""),
            J("""{"Osd":{"osdChannel":{"name":{"maxLen":31},"pos":["Upper Left","Top Center","Upper Right","Lower Left","Bottom Center","Lower Right"]},"osdTime":{"pos":["Upper Left","Top Center","Upper Right","Lower Left","Bottom Center","Lower Right"]}}}"""));
        _data["GetEnc"] = (J("""{"Enc":{"audio":0,"channel":0,"mainStream":{"bitRate":6144,"frameRate":25,"gop":2,"height":2160,"profile":"High","size":"3840*2160","vType":"h265","width":3840},"subStream":{"bitRate":256,"frameRate":10,"gop":4,"height":360,"profile":"High","size":"640*360","vType":"h264","width":640}}}"""),
            J("""{"Enc":[{"chnBit":1,"mainStream":{"size":"3840*2160","frameRate":[25,22,20,18,16,15,12,10,8,6,4,2],"bitRate":[4096,5120,6144,7168,8192],"profile":["Base","Main","High"]},"subStream":{"size":"640*360","frameRate":[15,10,7,4],"bitRate":[64,128,160,192,256,384,512],"profile":["Base","Main","High"]}},{"chnBit":1,"mainStream":{"size":"2560*1440","frameRate":[25,22,20,18,16,15,12,10,8,6,4,2],"bitRate":[1024,1536,2048,3072,4096,5120,6144,7168,8192],"profile":["Base","Main","High"]},"subStream":{"size":"640*360","frameRate":[15,10,7,4],"bitRate":[64,128,160,192,256,384,512],"profile":["Base","Main","High"]}}]}"""));
        _data["GetTime"] = (J("""{"Dst":{"enable":1,"endHour":3,"endMin":0,"endMon":10,"endSec":0,"endWeek":5,"endWeekday":0,"offset":1,"startHour":2,"startMin":0,"startMon":3,"startSec":0,"startWeek":5,"startWeekday":0},"Time":{"day":17,"hour":10,"hourFmt":0,"min":30,"mon":11,"sec":2,"timeFmt":"DD/MM/YYYY","timeZone":-3600,"year":2026}}"""),
            J("""{"Time":{"timeFmt":["DD/MM/YYYY","MM/DD/YYYY","YYYY/MM/DD"],"hourFmt":{"min":0,"max":1}}}"""));
        _data["GetNtp"] = (J("""{"Ntp":{"enable":1,"interval":1440,"port":123,"server":"pool.ntp.org"}}"""), J("""{"Ntp":{"interval":{"min":60,"max":65535},"port":{"min":1,"max":65535},"server":{"maxLen":127}}}"""));
        _data["GetMdAlarm"] = (J("""{"MdAlarm":{"channel":0,"newSens":{"sensDef":41,"sens":[]},"useNewSens":1}}"""), J("""{"MdAlarm":{"newSens":{"sensDef":{"min":0,"max":50}}}}"""));
        _data["GetAiAlarm"] = (J("""{"AiAlarm":{"ai_type":"people","sensitivity":60,"stay_time":0,"min_target_height":0,"max_target_height":1,"min_target_width":0,"max_target_width":1}}"""), null);
        _data["GetRec"] = (J("""{"Rec":{"channel":0,"overwrite":1,"packTime":"60 Minutes","postRec":"1 Minute","preRec":1,"schedule":{"enable":1,"table":"__TABLE__"}}}""".Replace("__TABLE__", Sched('1'))),
            J("""{"Rec":{"packTime":["30 Minutes","45 Minutes","60 Minutes"],"postRec":["15 Seconds","30 Seconds","1 Minute","2 Minutes","5 Minutes","10 Minutes"]}}"""));
        _data["GetFtpV20"] = (J("""{"Ftp":{"anonymous":0,"autoDir":1,"bpicSingle":0,"bvideoSingle":0,"interval":30,"maxSize":100,"mode":0,"onlyFtps":1,"password":"","picCaptureMode":3,"picInterval":60,"port":21,"remoteDir":"kamera1","scheduleEnable":1,"schedule":{"channel":0,"table":{"AI_PEOPLE":"__S0__","AI_VEHICLE":"__S0__","MD":"__S1__","TIMING":"__S0__"}},"server":"192.0.2.10","streamType":0,"userName":"ftpuser"}}""".Replace("__S0__", Sched('0')).Replace("__S1__", Sched('1'))),
            // Ranges in mixed formats as seen on real cameras: list, plain value, {min,max}, {maxLen}
            J("""{"Ftp":{"port":{"min":1,"max":65535},"mode":[0,1,2],"interval":30,"maxSize":[10,100,1024],"streamType":{"min":0,"max":1},"userName":{"maxLen":31},"server":{"maxLen":127},"picInterval":[30,60,120],"remoteDir":"x"}}"""));
        _data["GetEmailV20"] = (J("""{"Email":{"addr1":"a@example.com","addr2":"","addr3":"","attachmentType":"picture","interval":"5 Minutes","nickName":"Kamera","password":"","smtpPort":465,"smtpServer":"smtp.example.com","ssl":1,"userName":"a@example.com","scheduleEnable":0,"schedule":{"channel":0,"table":{"MD":"__S1__","AI_PEOPLE":"__S1__"}}}}""".Replace("__S1__", Sched('1'))),
            J("""{"Email":{"attachmentType":["picture","video","onlyText"],"interval":["30 Seconds","1 Minute","5 Minutes","10 Minutes"]}}"""));
        _data["GetPushV20"] = (J("""{"Push":{"scheduleEnable":1,"schedule":{"channel":0,"table":{"MD":"__S1__","AI_PEOPLE":"__S1__"}}}}""".Replace("__S1__", Sched('1'))), null);
        _data["GetAbility"] = (J("""{"Ability":{"abilityChn":[{"ptzCtrl":{"permit":7,"ver":1},"ptzPreset":{"permit":7,"ver":1}}]}}"""), null);
        _data["GetPtzPreset"] = (J("""{"PtzPreset":[{"channel":0,"enable":1,"id":1,"name":"Tor"},{"channel":0,"enable":0,"id":2,"name":"pos2"}]}"""), null);
        // Not supported by this simulated model: GetPowerLed, GetWhiteLed, GetRecV20, GetFtp, GetEmail, GetPush
    }

    /// <summary>Responses captured from a real camera with older firmware (server name anonymized).</summary>
    private void SeedLegacy()
    {
        foreach (var k in new[] { "GetFtpV20", "GetEmailV20", "GetPushV20" }) _data.Remove(k);
        _data["GetFtp"] = (J("""{"Ftp":{"anonymous":0,"interval":30,"maxSize":400,"mode":0,"password":"","port":21,"remoteDir":"/camera1/","schedule":{"enable":1,"table":"__S2__"},"server":"ftp.example.com","streamType":0,"userName":"ftpuser"}}""".Replace("__S2__", Sched('2'))),
            J("""{"Ftp":{"anonymous":"boolean","interval":[15,30,60,300,600,1800],"maxSize":{"max":1024,"min":10},"mode":{"max":2,"min":0},"password":"***","port":{"max":65535,"min":1},"remoteDir":{"maxLen":255},"schedule":{"enable":"boolean","table":{"maxLen":168,"minLen":168}},"server":{"maxLen":127},"streamType":{"max":2,"min":0},"userName":{"maxLen":127}}}"""));
        _data["GetEmail"] = (J("""{"Email":{"addr1":"","addr2":"","addr3":"","attachmentType":1,"interval":"5 Minutes","nickName":"","password":"","schedule":{"enable":1,"table":"__S1__"},"smtpPort":465,"smtpServer":"smtp.example.com","ssl":1,"supportTextType":1,"supportVideo":1,"textType":1,"userName":""}}""".Replace("__S1__", Sched('1'))),
            J("""{"Email":{"addr1":{"maxLen":127},"attachmentType":1,"interval":["30 Seconds","1 Minute","5 Minutes","10 Minutes","30 Minutes"],"schedule":{"enable":"boolean","table":{"maxLen":168,"minLen":168}},"smtpPort":{"max":65535,"min":1},"ssl":"boolean","textType":1}}"""));
        _data["GetPush"] = (J("""{"Push":{"schedule":{"enable":1,"table":"__S1__"}}}""".Replace("__S1__", Sched('1'))), J("""{"Push":{"schedule":{"enable":"boolean","table":{"maxLen":168,"minLen":168}}}}"""));
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            try { Handle(ctx); } catch { ctx.Response.StatusCode = 500; ctx.Response.Close(); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        string body;
        using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
        var req = JsonNode.Parse(body)!.AsArray()[0]!.AsObject();
        var cmd = req["cmd"]!.GetValue<string>();
        var param = req["param"];
        AllCommands.Add(cmd);
        bool hasToken = ctx.Request.QueryString["token"] == "TESTTOKEN";

        JsonObject resp;
        if (cmd == "Login")
        {
            Logins++;
            var user = param?["User"];
            bool ok = user?["userName"]?.GetValue<string>() == "admin" && user?["password"]?.GetValue<string>() == "pw";
            resp = ok ? J("""{"cmd":"Login","code":0,"value":{"Token":{"leaseTime":3600,"name":"TESTTOKEN"}}}""")
                      : J("""{"cmd":"Login","code":1,"error":{"detail":"login failed","rspCode":-7}}""");
        }
        else if (!hasToken)
            resp = J("""{"cmd":"__CMD__","code":1,"error":{"detail":"please login first","rspCode":-6}}""".Replace("__CMD__", cmd));
        else if (cmd == "Logout") { Logouts++; resp = J("""{"cmd":"Logout","code":0,"value":{"rspCode":200}}"""); }
        else if (cmd.StartsWith("Set") || cmd.StartsWith("Test") || cmd is "PtzCtrl" or "Reboot" or "AudioAlarmPlay")
        {
            SetCalls.Add((cmd, param?.DeepClone()));
            resp = J("""{"cmd":"__CMD__","code":0,"value":{"rspCode":200}}""".Replace("__CMD__", cmd));
        }
        else if (_data.TryGetValue(cmd, out var d))
        {
            resp = new JsonObject { ["cmd"] = cmd, ["code"] = 0, ["value"] = d.Value.DeepClone() };
            if (req["action"]?.GetValue<int>() == 1 && d.Range != null) resp["range"] = d.Range.DeepClone();
        }
        else
            resp = J("""{"cmd":"__CMD__","code":1,"error":{"detail":"not support","rspCode":-9}}""".Replace("__CMD__", cmd));

        var bytes = Encoding.UTF8.GetBytes(new JsonArray(resp).ToJsonString());
        ctx.Response.ContentType = "application/json";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
