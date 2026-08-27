namespace XinSpect;

/// <summary>
/// 精簡但「正確」的西洋棋走法產生器（10×12 mailbox 表示），用於 perft 節點吞吐量測。
/// 每個工作執行緒各自持有一份實例（獨立棋盤與走法緩衝），因此完全無鎖、無競爭，
/// 可線性擴充到任意（含超額）執行緒數。節點計數 = 搜尋樹中造訪的合法局面數（含內部節點與葉節點）。
///
/// 正確性：本產生器實作吃過路兵、王車易位、兵升變與完整合法性過濾（走子後檢查己方王是否被將軍），
/// 故 perft(n) 之葉節點數與公認值相符（起始局面 depth1=20、depth2=400、depth3=8902、depth4=197281）。
/// 這是真正的西洋棋走子運算，而非任意迴圈，量得的節點/秒具實質意義。
/// </summary>
internal sealed class ChessEngine
{
    private const int W = 0, B = 8;   // 顏色位元（同時作為「該方」標記）
    private const int OFF = -1;       // 邊界哨兵
    // 棋子編碼：1=兵 2=馬 3=象 4=車 5=后 6=王；黑方 +8。空=0，邊界=-1。

    private static readonly int[] KnightO = { -21, -19, -12, -8, 8, 12, 19, 21 };
    private static readonly int[] KingO = { -11, -10, -9, -1, 1, 9, 10, 11 };
    private static readonly int[] BishopO = { -11, -9, 9, 11 };
    private static readonly int[] RookO = { -10, -1, 1, 10 };

    private readonly int[] _b = new int[120];
    private readonly int[] _clear = new int[120];
    private readonly Move[][] _pool;

    private int _side;
    private int _ep;       // 過路兵目標格；0 表示無
    private int _castle;   // 位元遮罩：1=白王翼 2=白后翼 4=黑王翼 8=黑后翼
    private long _nodes;

    private struct Move { public short From, To; public sbyte Promo, Flag; } // Flag：0一般 1雙步 2過路兵 3易位
    private struct Undo { public int Ep, Castle, Captured, CapSq; }

    public ChessEngine(int maxDepth = 6)
    {
        _pool = new Move[maxDepth + 1][];
        for (int i = 0; i <= maxDepth; i++) _pool[i] = new Move[256];

        _clear[25] = 1 | 2;   // e1
        _clear[21] = 2;       // a1
        _clear[28] = 1;       // h1
        _clear[95] = 4 | 8;   // e8
        _clear[91] = 8;       // a8
        _clear[98] = 4;       // h8

        Reset();
    }

    /// <summary>重設為起始局面。</summary>
    public void Reset()
    {
        for (int i = 0; i < 120; i++) _b[i] = OFF;
        for (int r = 0; r < 8; r++)
            for (int f = 0; f < 8; f++)
                _b[21 + r * 10 + f] = 0;

        int[] back = { 4, 2, 3, 5, 6, 3, 2, 4 };   // R N B Q K B N R
        for (int f = 0; f < 8; f++)
        {
            _b[21 + f] = back[f];        // 白后排
            _b[31 + f] = 1;              // 白兵
            _b[81 + f] = 1 | 8;          // 黑兵
            _b[91 + f] = back[f] | 8;    // 黑后排
        }
        _side = W; _ep = 0; _castle = 1 | 2 | 4 | 8; _nodes = 0;
    }

    /// <summary>從目前局面搜尋到指定深度，回傳造訪節點總數（每次呼叫前歸零）。</summary>
    public long PerftNodes(int depth)
    {
        _nodes = 0;
        Search(depth);
        return _nodes;
    }

    private void Search(int depth)
    {
        _nodes++;
        if (depth == 0) return;

        Move[] mv = _pool[depth];
        int n = Generate(mv);
        int me = _side;
        for (int i = 0; i < n; i++)
        {
            Make(in mv[i], out var u);
            if (!IsAttacked(FindKing(me), _side)) Search(depth - 1);
            Unmake(in mv[i], in u);
        }
    }

    // ---- 走法產生 -----------------------------------------------------------

    private bool Enemy(int sq) => _b[sq] > 0 && (_b[sq] & 8) != _side;

    private int Generate(Move[] mv)
    {
        int n = 0;
        int fwd = _side == W ? 10 : -10;

        for (int sq = 21; sq <= 98; sq++)
        {
            int p = _b[sq];
            if (p <= 0 || (p & 8) != _side) continue;   // 空、邊界或非我方

            switch (p & 7)
            {
                case 1: // 兵
                    {
                        int one = sq + fwd;
                        if (_b[one] == 0)
                        {
                            if (IsPromoRank(one)) AddPromos(mv, ref n, sq, one, 0);
                            else
                            {
                                Add(mv, ref n, sq, one, 0, 0);
                                bool onStart = _side == W ? (sq >= 31 && sq <= 38) : (sq >= 81 && sq <= 88);
                                if (onStart && _b[sq + 2 * fwd] == 0) Add(mv, ref n, sq, sq + 2 * fwd, 0, 1);
                            }
                        }
                        PawnCapture(mv, ref n, sq, sq + fwd - 1);
                        PawnCapture(mv, ref n, sq, sq + fwd + 1);
                        break;
                    }
                case 2: // 馬
                    for (int i = 0; i < 8; i++) { int to = sq + KnightO[i]; if (_b[to] == 0 || Enemy(to)) Add(mv, ref n, sq, to, 0, 0); }
                    break;
                case 3: Slide(mv, ref n, sq, BishopO); break;
                case 4: Slide(mv, ref n, sq, RookO); break;
                case 5: Slide(mv, ref n, sq, BishopO); Slide(mv, ref n, sq, RookO); break;
                case 6: // 王
                    for (int i = 0; i < 8; i++) { int to = sq + KingO[i]; if (_b[to] == 0 || Enemy(to)) Add(mv, ref n, sq, to, 0, 0); }
                    Castle(mv, ref n, sq);
                    break;
            }
        }
        return n;
    }

    private void PawnCapture(Move[] mv, ref int n, int sq, int to)
    {
        if (Enemy(to))
        {
            if (IsPromoRank(to)) AddPromos(mv, ref n, sq, to, 0);
            else Add(mv, ref n, sq, to, 0, 0);
        }
        else if (to == _ep && _ep != 0)
        {
            Add(mv, ref n, sq, to, 0, 2);   // 過路兵
        }
    }

    private void Slide(Move[] mv, ref int n, int sq, int[] dirs)
    {
        for (int i = 0; i < dirs.Length; i++)
        {
            int t = sq + dirs[i];
            while (_b[t] == 0) { Add(mv, ref n, sq, t, 0, 0); t += dirs[i]; }
            if (Enemy(t)) Add(mv, ref n, sq, t, 0, 0);
        }
    }

    private void Castle(Move[] mv, ref int n, int sq)
    {
        if (_side == W && sq == 25)
        {
            if ((_castle & 1) != 0 && _b[26] == 0 && _b[27] == 0 && _b[28] == 4
                && !IsAttacked(25, B) && !IsAttacked(26, B) && !IsAttacked(27, B))
                Add(mv, ref n, 25, 27, 0, 3);
            if ((_castle & 2) != 0 && _b[24] == 0 && _b[23] == 0 && _b[22] == 0 && _b[21] == 4
                && !IsAttacked(25, B) && !IsAttacked(24, B) && !IsAttacked(23, B))
                Add(mv, ref n, 25, 23, 0, 3);
        }
        else if (_side == B && sq == 95)
        {
            if ((_castle & 4) != 0 && _b[96] == 0 && _b[97] == 0 && _b[98] == (4 | 8)
                && !IsAttacked(95, W) && !IsAttacked(96, W) && !IsAttacked(97, W))
                Add(mv, ref n, 95, 97, 0, 3);
            if ((_castle & 8) != 0 && _b[94] == 0 && _b[93] == 0 && _b[92] == 0 && _b[91] == (4 | 8)
                && !IsAttacked(95, W) && !IsAttacked(94, W) && !IsAttacked(93, W))
                Add(mv, ref n, 95, 93, 0, 3);
        }
    }

    private bool IsPromoRank(int sq) => _side == W ? (sq >= 91 && sq <= 98) : (sq >= 21 && sq <= 28);

    private void AddPromos(Move[] mv, ref int n, int from, int to, int flag)
    {
        Add(mv, ref n, from, to, 5, flag);
        Add(mv, ref n, from, to, 4, flag);
        Add(mv, ref n, from, to, 3, flag);
        Add(mv, ref n, from, to, 2, flag);
    }

    private static void Add(Move[] mv, ref int n, int from, int to, int promo, int flag)
    {
        mv[n].From = (short)from; mv[n].To = (short)to; mv[n].Promo = (sbyte)promo; mv[n].Flag = (sbyte)flag; n++;
    }

    // ---- 走子 / 還原 --------------------------------------------------------

    private void Make(in Move m, out Undo u)
    {
        u.Ep = _ep; u.Castle = _castle; u.Captured = 0; u.CapSq = 0;
        int piece = _b[m.From];
        int fwd = _side == W ? 10 : -10;

        if (m.Flag == 2)              // 過路兵：被吃兵在目標格後方
        {
            int capSq = m.To - fwd;
            u.Captured = _b[capSq]; u.CapSq = capSq; _b[capSq] = 0;
        }
        else if (_b[m.To] != 0)
        {
            u.Captured = _b[m.To]; u.CapSq = m.To;
        }

        _b[m.To] = m.Promo != 0 ? ((m.Promo & 7) | _side) : piece;
        _b[m.From] = 0;

        if (m.Flag == 3)              // 王車易位：搬車
        {
            switch (m.To)
            {
                case 27: _b[26] = _b[28]; _b[28] = 0; break;   // 白王翼 h1→f1
                case 23: _b[24] = _b[21]; _b[21] = 0; break;   // 白后翼 a1→d1
                case 97: _b[96] = _b[98]; _b[98] = 0; break;   // 黑王翼 h8→f8
                case 93: _b[94] = _b[91]; _b[91] = 0; break;   // 黑后翼 a8→d8
            }
        }

        _ep = m.Flag == 1 ? (m.From + fwd) : 0;
        _castle &= ~(_clear[m.From] | _clear[m.To]);
        _side ^= 8;
    }

    private void Unmake(in Move m, in Undo u)
    {
        _side ^= 8;   // 回到走子方

        _b[m.From] = m.Promo != 0 ? (1 | _side) : _b[m.To];
        _b[m.To] = 0;

        if (m.Flag == 3)
        {
            switch (m.To)
            {
                case 27: _b[28] = _b[26]; _b[26] = 0; break;
                case 23: _b[21] = _b[24]; _b[24] = 0; break;
                case 97: _b[98] = _b[96]; _b[96] = 0; break;
                case 93: _b[91] = _b[94]; _b[94] = 0; break;
            }
        }

        if (u.Captured != 0) _b[u.CapSq] = u.Captured;
        _ep = u.Ep; _castle = u.Castle;
    }

    // ---- 攻擊偵測 -----------------------------------------------------------

    private int FindKing(int color)
    {
        int k = 6 | color;
        for (int sq = 21; sq <= 98; sq++) if (_b[sq] == k) return sq;
        return 21;   // 理論上不會發生
    }

    private bool IsAttacked(int sq, int by)
    {
        // 兵
        if (by == W) { if (_b[sq - 11] == 1 || _b[sq - 9] == 1) return true; }
        else { if (_b[sq + 11] == (1 | 8) || _b[sq + 9] == (1 | 8)) return true; }

        int kn = 2 | by, kg = 6 | by, bp = 3 | by, rk = 4 | by, q = 5 | by;

        for (int i = 0; i < 8; i++) if (_b[sq + KnightO[i]] == kn) return true;
        for (int i = 0; i < 8; i++) if (_b[sq + KingO[i]] == kg) return true;

        for (int i = 0; i < 4; i++)
        {
            int t = sq + BishopO[i];
            while (_b[t] == 0) t += BishopO[i];
            if (_b[t] == bp || _b[t] == q) return true;
        }
        for (int i = 0; i < 4; i++)
        {
            int t = sq + RookO[i];
            while (_b[t] == 0) t += RookO[i];
            if (_b[t] == rk || _b[t] == q) return true;
        }
        return false;
    }
}
