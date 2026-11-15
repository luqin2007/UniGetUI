namespace UniGetUI.Extensions.CustomChannels.Pan;

/// <summary>
/// 网盘接口地址集中定义。网盘接口属于非公开接口，可能随官方调整而变化，
/// 因此全部集中在此处，便于失效时快速修正。
/// </summary>
public static class PanApiEndpoints
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public static class Baidu
    {
        public const string Origin = "https://pan.baidu.com";
        public const string Referer = "https://pan.baidu.com/disk/home";
        public const string AppId = "250528";

        public static string Verify(string surl, long timestamp) =>
            $"{Origin}/share/verify?surl={surl}&t={timestamp}&channel=chunlei&web=1&app_id={AppId}&clienttype=0";

        public static string ListShare(string surl, string dir, int page = 1, int size = 100) =>
            $"{Origin}/share/list?shorturl={surl}&dir={Uri.EscapeDataString(dir)}&root={(dir == "/" ? 1 : 0)}" +
            $"&web=1&app_id={AppId}&channel=chunlei&clienttype=0&page={page}&num={size}";

        public static string Transfer(string shareId, string uk, string sekey) =>
            $"{Origin}/share/transfer?shareid={shareId}&from={uk}&sekey={Uri.EscapeDataString(sekey)}" +
            "&ondup=newcopy&async=1&channel=chunlei&web=1&app_id=" + AppId + "&clienttype=0";

        public static string ListOwn(string dir) =>
            $"{Origin}/api/list?dir={Uri.EscapeDataString(dir)}&order=name&start=0&limit=1000" +
            $"&web=1&app_id={AppId}&channel=chunlei&clienttype=0";

        public static string TemplateVariable =>
            $"{Origin}/api/gettemplatevariable?clienttype=0&app_id={AppId}&web=1&fields=%5B%22bdstoken%22%5D";

        public static string FileManager(string bdstoken) =>
            $"{Origin}/api/filemanager?opera=delete&async=1&onnest=fail&web=1&app_id={AppId}" +
            $"&channel=chunlei&clienttype=0&bdstoken={bdstoken}";
    }

    public static class Quark
    {
        public const string Origin = "https://pan.quark.cn";
        public const string ApiBase = "https://drive-pc.quark.cn/1/clouddrive";
        public const string CommonQuery = "pr=ucpro&fr=pc&uc_param_str=";

        public static string ShareToken =>
            $"{ApiBase}/share/sharepage/token?{CommonQuery}";

        public static string ShareDetail(string pwdId, string stoken, string pdirFid = "0", int page = 1, int size = 200) =>
            $"{ApiBase}/share/sharepage/detail?{CommonQuery}&pwd_id={pwdId}&stoken={Uri.EscapeDataString(stoken)}" +
            $"&pdir_fid={Uri.EscapeDataString(pdirFid)}&force=0&_page={page}&_size={size}&_fetch_banner=0&_fetch_share=0&_fetch_total=1" +
            "&_sort=file_type:asc,updated_at:desc";

        public static string ShareSave => $"{ApiBase}/share/sharepage/save?{CommonQuery}";

        public static string Task(string taskId) =>
            $"{ApiBase}/task?{CommonQuery}&task_id={Uri.EscapeDataString(taskId)}&retry_index=0";

        public static string FileSort(string parentFid, int page = 1, int size = 200) =>
            $"{ApiBase}/file/sort?{CommonQuery}&pdir_fid={parentFid}&_page={page}&_size={size}" +
            "&_fetch_total=1&_sort=file_type:asc,updated_at:desc";

        public static string FileCreate => $"{ApiBase}/file?{CommonQuery}";

        public static string FileDownload => $"{ApiBase}/file/download?{CommonQuery}";

        public static string FileDelete => $"{ApiBase}/file/delete?{CommonQuery}";

        /// <summary>最近使用列表：登录态校验用的轻量接口（/members 在 drive-pc 上不存在）。</summary>
        public static string RecentList => $"{ApiBase}/behavior/recent/list?{CommonQuery}&item_size=20";
    }
}
