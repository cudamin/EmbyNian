# 生成标题栏那五颗「细线」图标的 PathIcon.Data（「改成这种风格」）。
# 一次性的工具：几何全是解析算出来的，手算这些交点会错，而错一位在 20 像素的图标上就是歪一格。
# 用法：awk -f tools/line-icons.awk
#
# 画法：线条图标 = 一条 1.4 粗的笔画。直笔画用「胶囊」（两头各半个圆，所以是圆头）；
# 闭合的轮廓（齿轮、镜片圈）用「外框 + 反向内框」，反向那一圈把中间掏空，于是看着就是一条线。
BEGIN {
    PI = 3.14159265358979
    W = 1.4          # 线粗
    R = W / 2        # 胶囊半径
    OFS = ""

    print "PANE\t"    pane()
    print "GEAR\t"    gear()
    print "SEARCH\t"  search()
    print "BACK\t"    back()
    print "FORWARD\t" forward()
}

function f(v) {
    s = sprintf("%.2f", v)
    sub(/0+$/, "", s)
    sub(/\.$/, "", s)
    if (s == "-0") s = "0"
    return s
}

# 一颗胶囊：P→Q 的直边，两头各扣半个圆。
function cap(x1, y1, x2, y2, r,    dx, dy, L, mx, my) {
    dx = x2 - x1; dy = y2 - y1; L = sqrt(dx * dx + dy * dy)
    mx = dy / L * r; my = -dx / L * r
    return "M " f(x1 + mx) "," f(y1 + my) \
        " L " f(x2 + mx) "," f(y2 + my) \
        " A " f(r) "," f(r) " 0 0 1 " f(x2 - mx) "," f(y2 - my) \
        " L " f(x1 - mx) "," f(y1 - my) \
        " A " f(r) "," f(r) " 0 0 1 " f(x1 + mx) "," f(y1 + my) " Z"
}

# 一个圆：sweep=1 是填，sweep=0 是掏洞（同一套非零环绕规则）。
function circle(cx, cy, r, sweep) {
    return "M " f(cx) "," f(cy - r) \
        " A " f(r) "," f(r) " 0 0 " sweep " " f(cx) "," f(cy + r) \
        " A " f(r) "," f(r) " 0 0 " sweep " " f(cx) "," f(cy - r) " Z"
}

# 一圈圆角矩形。sweep=1 顺着走（填），sweep=0 倒着走（掏洞）—— 和 circle 一样的规矩。
function rrect(x1, y1, x2, y2, r, sweep) {
    if (sweep == 1)
        return "M " f(x1 + r) "," f(y1) \
            " L " f(x2 - r) "," f(y1) " A " f(r) "," f(r) " 0 0 1 " f(x2) "," f(y1 + r) \
            " L " f(x2) "," f(y2 - r) " A " f(r) "," f(r) " 0 0 1 " f(x2 - r) "," f(y2) \
            " L " f(x1 + r) "," f(y2) " A " f(r) "," f(r) " 0 0 1 " f(x1) "," f(y2 - r) \
            " L " f(x1) "," f(y1 + r) " A " f(r) "," f(r) " 0 0 1 " f(x1 + r) "," f(y1) " Z"

    return "M " f(x1 + r) "," f(y1) \
        " A " f(r) "," f(r) " 0 0 0 " f(x1) "," f(y1 + r) \
        " L " f(x1) "," f(y2 - r) " A " f(r) "," f(r) " 0 0 0 " f(x1 + r) "," f(y2) \
        " L " f(x2 - r) "," f(y2) " A " f(r) "," f(r) " 0 0 0 " f(x2) "," f(y2 - r) \
        " L " f(x2) "," f(y1 + r) " A " f(r) "," f(r) " 0 0 0 " f(x2 - r) "," f(y1) " Z"
}

# 侧边栏那颗：一块面板的轮廓，左边用一竖分出一条窄栏 —— 用户给的就是这个形状，画的是「侧边栏在不在」，
# 不是三条横线那种通用菜单。轮廓 = 外框顺着走一圈 ＋ 内框倒着走一圈（内框四边各往里 1.4，圆角半径也跟着
# 减 1.4），剩下的就是一条 1.4 粗的线。那一竖是胶囊，两端算到外框的边上：圆头整个埋在上下两条边的线里，
# 所以看不出它是圆的，也不会在框外露出一截。
# 竖线放在面板宽度的三分之一处：窄栏 4.7 宽，和真侧边栏收起时那条 48 像素的窄条一个意思。
function pane(    x1, y1, x2, y2, ro) {
    x1 = 2.9; y1 = 4.2; x2 = 17.1; y2 = 15.8; ro = 2.4
    return rrect(x1, y1, x2, y2, ro, 1) " " \
        rrect(x1 + W, y1 + W, x2 - W, y2 - W, ro - W, 0) " " \
        cap(x1 + (x2 - x1) / 3, y1 + R, x1 + (x2 - x1) / 3, y2 - R, R)
}

# 放大镜：一个圈（外 5.5、内 4.1 = 1.4 的线）＋ 一根圆头的柄。柄的内端 12.4,12.4 离圆心 5.52，
# 减掉半径 0.7 还有 4.82，够不到 4.1 那个洞 —— 不然镜片里会多出一小截。
function search() {
    return circle(8.5, 8.5, 5.5, 1) " " circle(8.5, 8.5, 4.1, 0) " " cap(12.4, 12.4, 16.7, 16.7, R)
}

function back() {
    return cap(3.7, 10, 16.3, 10, R) " " cap(3.7, 10, 9.3, 4.4, R) " " cap(3.7, 10, 9.3, 15.6, R)
}

function forward() {
    return cap(16.3, 10, 3.7, 10, R) " " cap(16.3, 10, 10.7, 4.4, R) " " cap(16.3, 10, 10.7, 15.6, R)
}

# 极坐标取点，结果放在 PX/PY（awk 的函数只能返一个值）。
function pol(r, deg) {
    PX = 10 + r * cos(deg * PI / 180)
    PY = 10 + r * sin(deg * PI / 180)
}

# 两条直线的交点，结果放在 IX/IY。几乎平行就退回第一条的基点。
function isect(b1x, b1y, t1x, t1y, b2x, b2y, t2x, t2y,    det, a, ddx, ddy) {
    det = -t1x * t2y + t2x * t1y
    if (det < 1e-9 && det > -1e-9) { IX = b1x; IY = b1y; return }
    ddx = b2x - b1x; ddy = b2y - b1y
    a = (-ddx * t2y + t2x * ddy) / det
    IX = b1x + a * t1x; IY = b1y + a * t1y
}

# 轮廓上一个拐角往里收 w 之后的落点：两条边各按自己的「朝里」法线平移 w，再求交。
# 弧那一边用它在这一点的切线代替，短距离上差不到十分之一像素。
function corner(px, py, t1x, t1y, t2x, t2y, w,    l1, l2) {
    l1 = sqrt(t1x * t1x + t1y * t1y); t1x /= l1; t1y /= l1
    l2 = sqrt(t2x * t2x + t2y * t2y); t2x /= l2; t2y /= l2
    isect(px + w * -t1y, py + w * t1x, t1x, t1y, px + w * -t2y, py + w * t2x, t2x, t2y)
}

# 齿轮：一圈齿形的轮廓线，中间一颗轴心的圈。轮廓线 = 外框顺着走一圈 ＋ 内框倒着走一圈，
# 倒着那一圈把中间掏空，剩下的就是一条线。
# 六颗齿、线细一档（「把左上角的齿轮改回6个齿轮，线条细一点」）：这一颗用自己的 w = 1.1，是这一行里唯一
# 不用 W 的图标 —— 齿距 60° 时齿间那条槽只有 26°，在 6.5 的半径上 2.95 像素，线越粗槽里剩的黑越少，
# 1.4 那一档六颗齿就糊成了一圈花边。齿顶半角 14°、齿根半角 17°：齿往上收窄 3°，所以槽从下往上是敞开的。
function gear(    w, N, Rt, Rr, th, rh, k, kk, tc, d) {
    w = 1.1; N = 6; Rt = 8.7; Rr = 6.5; th = 14; rh = 17

    for (k = 0; k < N; k++) {
        tc = -90 + k * 360 / N                       # 一颗齿朝正上
        AA[k] = tc - rh; BA[k] = tc - th; CA[k] = tc + th; DA[k] = tc + rh
        pol(Rr, AA[k]); AX[k] = PX; AY[k] = PY       # 齿根，进
        pol(Rt, BA[k]); BX[k] = PX; BY[k] = PY       # 齿顶，进
        pol(Rt, CA[k]); CX[k] = PX; CY[k] = PY       # 齿顶，出
        pol(Rr, DA[k]); DX[k] = PX; DY[k] = PY       # 齿根，出
    }

    for (k = 0; k < N; k++) {
        corner(AX[k], AY[k], -sin(AA[k] * PI / 180), cos(AA[k] * PI / 180), BX[k] - AX[k], BY[k] - AY[k], w)
        IAX[k] = IX; IAY[k] = IY
        corner(BX[k], BY[k], BX[k] - AX[k], BY[k] - AY[k], -sin(BA[k] * PI / 180), cos(BA[k] * PI / 180), w)
        IBX[k] = IX; IBY[k] = IY
        corner(CX[k], CY[k], -sin(CA[k] * PI / 180), cos(CA[k] * PI / 180), DX[k] - CX[k], DY[k] - CY[k], w)
        ICX[k] = IX; ICY[k] = IY
        corner(DX[k], DY[k], DX[k] - CX[k], DY[k] - CY[k], -sin(DA[k] * PI / 180), cos(DA[k] * PI / 180), w)
        IDX[k] = IX; IDY[k] = IY
    }

    d = "M " f(AX[0]) "," f(AY[0])
    for (k = 0; k < N; k++) {
        kk = (k + 1) % N
        d = d " L " f(BX[k]) "," f(BY[k])
        d = d " A " f(Rt) "," f(Rt) " 0 0 1 " f(CX[k]) "," f(CY[k])
        d = d " L " f(DX[k]) "," f(DY[k])
        d = d " A " f(Rr) "," f(Rr) " 0 0 1 " f(AX[kk]) "," f(AY[kk])
    }
    d = d " Z M " f(IAX[0]) "," f(IAY[0])
    for (k = N - 1; k >= 0; k--) {
        d = d " A " f(Rr - w) "," f(Rr - w) " 0 0 0 " f(IDX[k]) "," f(IDY[k])
        d = d " L " f(ICX[k]) "," f(ICY[k])
        d = d " A " f(Rt - w) "," f(Rt - w) " 0 0 0 " f(IBX[k]) "," f(IBY[k])
        d = d " L " f(IAX[k]) "," f(IAY[k])
    }

    return d " Z " circle(10, 10, 2.9, 1) " " circle(10, 10, 2.9 - w, 0)
}
