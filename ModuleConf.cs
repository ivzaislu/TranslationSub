namespace TranslationSub;

public class ModuleConf
{
    public bool enable { get; set; } = true;
    public int check_interval_minutes { get; set; } = 15;

    // TranslationSub owns only its scheduling metadata. Online balancer
    // enable/host/token/proxy/cache configuration is read and enforced by the
    // native Lampac modules through their /lite/* metadata endpoints.

    // Same public TMDB API used by Lampa. Can be overridden in init.conf if needed.
    public string tmdb_apihost { get; set; } = "https://api.themoviedb.org/3";
    public string tmdb_apikey { get; set; } = "4ef0d7355d9ffb5151e987764708ce96";
}
