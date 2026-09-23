namespace EmbyNian.Playback;

/// <summary>
/// 「叫醒窗口的那一下点击不作数」这一条规矩（用户令 2026-09-23：「先点一下让窗口置顶，然后再点一下触发
/// 暂停/播放」）。
/// <para>
/// Windows 上内容区的通用规矩正是这个：激活点击只激活、不落在内容上。可 WinUI 把激活那一下当成一次正常的
/// <c>Tapped</c> 递到页面（实测：点别的窗口再点画面，按下与 Tapped 都到、暂停也跟着切了），所以这一下得由
/// 我们自己认出来、自己吃掉。
/// </para>
/// <para>
/// <b>判据的主料是「上一拍问出来的前台位」，不是「刚变前台多久」。</b>按下送到页面时窗口<b>已经</b>是前台了
/// （激活发生在按下之前，实测相隔只有 1~5ms），所以按下那一刻现问「是不是前台」永远答「是」。真正分得出
/// 「这一下之前我们在不在前台」的，只有<b>上一拍</b>——播放页每 100ms 问一次严格前台位（光标规则也要用），
/// 窗口在后台时那一拍读到的就是「不是前台」。上一拍不是前台、这一下按下时已是前台，这一下就是叫醒的那一下。
/// </para>
/// <para>
/// <b>头一版拿「窗口刚变前台 ≤250ms」当主料，错了。</b>那个「刚变前台的时刻」是每 100ms 一拍的轮询记的，
/// 而激活到按下只隔 1~5ms，轮询几乎<b>抓不到</b>那一瞬 —— 于是那个时刻一直是上一次变前台的<b>陈值</b>
/// （多半是几秒前起播那下），减出来远大于 250ms，叫醒那一下反被判成「普通点击」照常暂停/播放。用户实测
/// 「点一下就播了」正是这个。改用 <paramref name="wasForeground"/>（上一拍的前台位）就不吃这个亏。
/// </para>
/// <para>
/// <see cref="RegainedMilliseconds"/> 现在只剩兜底：万一轮询<b>真的</b>正好落在激活与按下那 1~5ms 之间、把
/// 上一拍前台位写成了 true，就靠「那一拍记下的变前台时刻离现在 ≤250ms」把这一下仍认成叫醒。代价是 Alt+Tab
/// 唤回之后 250ms 内的第一下点击也会被吃掉一次（与 Windows 自己那套激活规矩同款）。独占模式那一份写在
/// <c>assets/mpv-ui/scripts/uosc/main.lua</c> 的 <c>EMBYNIAN[click-pause-wake]</c>，判据是 mpv 的 <c>focused</c>。
/// </para>
/// </summary>
public static class WakeClick
{
    /// <summary>兜底窗口：轮询恰好抓到激活那一瞬时，用「刚变前台多久」再认一次 —— 播放页每拍 100ms，留够两拍半。</summary>
    public const long RegainedMilliseconds = 250;

    /// <summary>
    /// 这一下按下是不是「叫醒窗口的那一下」：窗口<b>此刻</b>是前台（<paramref name="foregroundNow"/>，激活已经
    /// 发生），而它<b>这一下之前</b>还不是 —— 由<b>上一拍</b>的前台位判定（<paramref name="wasForeground"/> 为
    /// false），或者退一步，那一拍<b>刚刚</b>才把它记成前台（<paramref name="foregroundForMilliseconds"/> ≤
    /// <see cref="RegainedMilliseconds"/>，专防轮询正好插在激活与按下之间的那一拍）。
    /// <para>
    /// 认错的代价是吃掉用户真想要的那一次点击（「点一下没反应」），所以 <paramref name="foregroundNow"/> 为假时
    /// 一律不算：没激活成前台的点击不可能是叫醒那一下。
    /// </para>
    /// </summary>
    public static bool IsWaking(bool foregroundNow, bool wasForeground, long foregroundForMilliseconds) =>
        foregroundNow
        && (!wasForeground || foregroundForMilliseconds is >= 0 and <= RegainedMilliseconds);
}
