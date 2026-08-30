namespace XinSpect;

/// <summary>
/// 中國象棋走法產生器（13×14 mailbox 表示），與 <see cref="ChessEngine"/> 同一用途：
/// 以 perft 作為確定性整數負載，量測節點吞吐，並且「順便驗證這台機器算得對不對」。
/// 每個工作執行緒各自持有一份實例（獨立棋盤與走法緩衝），完全無鎖、無競爭。
/// </summary>
/// <remarks>
/// 為什麼要多一套象棋：西洋棋 perft 的熱點在滑行子的直線掃描與升變分支；象棋不一樣——
/// 炮吃子得「越過恰好一個砲架、再往下找第一個子」（同方向掃兩段）、馬要查蹩腿、
/// 象要查象眼且不得過河、士與將困在九宮、兵過河才橫走、沒有升變，另加白臉將
/// （兩將不得在同一直線上照面）。分支型態與記憶體足跡都與西洋棋不同，是實質上的第二種負載。
///
/// 正確性錨點（起始局面的葉節點數，皆為公認值）：
/// depth1=44、depth2=1,920、depth3=79,666、depth4=3,290,240。
/// 這些是<b>數學常數</b>而不是量測值——所以跑分時可以反查：算出別的數字，就是這台機器算錯了。
/// </remarks>
internal sealed class XiangqiEngine : IPerftEngine
{
    // ── 棋盤表示 ──────────────────────────────────────────────────────────────
    // 列距 13：左右各 2 欄、上下各 2 列的界外哨兵，讓馬的 (±2,±1) 與象的 (±2,±2) 一律落在
    // 陣列範圍內，用哨兵值擋掉即可，不必逐步計算邊界。
    // 可走範圍：列 2–11（10 列）、欄 2–10（9 欄）；index = 列 * 13 + 欄。
    // 列 2 = 黑方底線（第 10 路），列 11 = 紅方底線（第 1 路）；河界在列 6 與列 7 之間。
    private const int S = 13;
    private const int Size = S * 14;
    private const int Lo = 2 * S + 2, Hi = 11 * S + 10;

    private const int Red = 0, Blk = 8;   // 顏色位元（同時作為「該方」標記）
    private const int OFF = -1;           // 界外哨兵

    // 棋子編碼：1=兵 2=馬 3=象 4=車 5=炮 6=士 7=將；黑方 +8。空=0，界外=-1。
    private const int Pawn = 1, Horse = 2, Eleph = 3, Rook = 4, Cannon = 5, Advisor = 6, King = 7;

    private static readonly int[] Ortho = { -S, S, -1, 1 };
    private static readonly int[] Diag = { -S - 1, -S + 1, S - 1, S + 1 };
    /// <summary>象走田：目的地位移，與 <see cref="Diag"/> 同序——其中點正好是象眼。</summary>
    private static readonly int[] ElephTo = { -2 * S - 2, -2 * S + 2, 2 * S - 2, 2 * S + 2 };
    /// <summary>馬走日：目的地位移（相對於馬）。</summary>
    private static readonly int[] HorseTo = { -2 * S - 1, -2 * S + 1, 2 * S - 1, 2 * S + 1, -S - 2, S - 2, -S + 2, S + 2 };
    /// <summary>與 <see cref="HorseTo"/> 同序的蹩馬腿格（相對於馬）。</summary>
    private static readonly int[] HorseLeg = { -S, -S, S, S, -1, -1, 1, 1 };

    /// <summary>底線排列（欄 2–10）：車馬象士將士象馬車。</summary>
    private static readonly int[] BackRank = { Rook, Horse, Eleph, Advisor, King, Advisor, Eleph, Horse, Rook };

    /// <summary>象棋任一局面的走法數上限遠低於此值；開足夠大就不必在產生器裡檢查溢出。</summary>
    private const int MaxMoves = 160;

    private readonly int[] _b = new int[Size];
    private readonly Move[][] _pool;
    private readonly int[] _kingSq = new int[2];   // [0]=紅帥 [1]=黑將，隨走子增量維護

    private int _side;
    private long _nodes;

    private struct Move { public short From, To; }

    public XiangqiEngine(int maxDepth = 6)
    {
        _pool = new Move[maxDepth + 1][];
        for (int i = 0; i <= maxDepth; i++) _pool[i] = new Move[MaxMoves];
        Reset();
    }

    /// <summary>重設為起始局面（紅先）。</summary>
    public void Reset()
    {
        for (int i = 0; i < Size; i++) _b[i] = OFF;
        for (int r = 2; r <= 11; r++)
            for (int c = 2; c <= 10; c++)
                _b[r * S + c] = 0;

        for (int c = 2; c <= 10; c++)
        {
            _b[2 * S + c] = BackRank[c - 2] | Blk;   // 黑方底線
            _b[11 * S + c] = BackRank[c - 2];        // 紅方底線
        }
        _b[4 * S + 3] = Cannon | Blk; _b[4 * S + 9] = Cannon | Blk;
        _b[9 * S + 3] = Cannon; _b[9 * S + 9] = Cannon;
        for (int c = 2; c <= 10; c += 2)
        {
            _b[5 * S + c] = Pawn | Blk;
            _b[8 * S + c] = Pawn;
        }

        _kingSq[0] = 11 * S + 6; _kingSq[1] = 2 * S + 6;
        _side = Red; _nodes = 0;
    }

    /// <summary>
    /// 從目前局面數到指定深度的<b>葉節點</b>數（perft 的標準定義，每次呼叫前歸零）。
    /// 起始局面的值是常數，可用來核對運算正確性。
    /// </summary>
    public long PerftLeaves(int depth)
    {
        _nodes = 0;
        Leaves(depth);
        return _nodes;
    }

    private void Leaves(int depth)
    {
        if (depth == 0) { _nodes++; return; }

        Move[] mv = _pool[depth];
        int n = Generate(mv);
        int me = _side;
        for (int i = 0; i < n; i++)
        {
            Make(in mv[i], out int cap);
            if (!KingInDanger(me)) Leaves(depth - 1);
            Unmake(in mv[i], cap);
        }
    }

    // ── 位置判定 ──────────────────────────────────────────────────────────────

    /// <summary>在該方九宮內（欄 5–7；紅列 9–11、黑列 2–4）。</summary>
    private static bool InPalace(int sq, int side)
    {
        int c = sq % S;
        if (c < 5 || c > 7) return false;
        int r = sq / S;
        return side == Red ? r >= 9 && r <= 11 : r >= 2 && r <= 4;
    }

    /// <summary>在該方本半場（未過河）。象不得過河。</summary>
    private static bool OwnHalf(int sq, int side) => side == Red ? sq / S >= 7 : sq / S <= 6;

    /// <summary>該格已在對方半場（兵過河後才能橫走）。</summary>
    private static bool Crossed(int sq, int side) => side == Red ? sq / S <= 6 : sq / S >= 7;

    private bool Enemy(int sq) => _b[sq] > 0 && (_b[sq] & 8) != _side;

    /// <summary>可落點：空格或敵子；界外（-1）兩者皆不成立，因此邊界檢查由哨兵一併完成。</summary>
    private bool CanLand(int sq) => _b[sq] == 0 || Enemy(sq);

    private static void Add(Move[] mv, ref int n, int from, int to)
    {
        mv[n].From = (short)from;
        mv[n].To = (short)to;
        n++;
    }

    // ── 走法產生 ──────────────────────────────────────────────────────────────

    private int Generate(Move[] mv)
    {
        int n = 0;
        int fwd = _side == Red ? -S : S;   // 紅往列號小的方向走

        for (int sq = Lo; sq <= Hi; sq++)
        {
            int p = _b[sq];
            if (p <= 0 || (p & 8) != _side) continue;   // 空格、界外或非我方

            switch (p & 7)
            {
                case Rook:
                    for (int d = 0; d < 4; d++)
                    {
                        int o = Ortho[d], t = sq + o;
                        while (_b[t] == 0) { Add(mv, ref n, sq, t); t += o; }
                        if (Enemy(t)) Add(mv, ref n, sq, t);
                    }
                    break;

                case Cannon:
                    for (int d = 0; d < 4; d++)
                    {
                        int o = Ortho[d], t = sq + o;
                        while (_b[t] == 0) { Add(mv, ref n, sq, t); t += o; }   // 不吃子時與車同
                        if (_b[t] == OFF) continue;                            // 這個方向沒有砲架
                        t += o;                                                // 越過砲架（恰好一個）
                        while (_b[t] == 0) t += o;                             // 架後第一個子才是目標
                        if (Enemy(t)) Add(mv, ref n, sq, t);
                    }
                    break;

                case Horse:
                    // 蹩腿格若在界外，其對應目的地必然也在界外，故一個哨兵檢查同時擋掉兩件事
                    for (int i = 0; i < 8; i++)
                        if (_b[sq + HorseLeg[i]] == 0 && CanLand(sq + HorseTo[i]))
                            Add(mv, ref n, sq, sq + HorseTo[i]);
                    break;

                case Eleph:
                    // 象眼被塞住不能走；且象不過河
                    for (int i = 0; i < 4; i++)
                    {
                        if (_b[sq + Diag[i]] != 0) continue;
                        int t = sq + ElephTo[i];
                        if (CanLand(t) && OwnHalf(t, _side)) Add(mv, ref n, sq, t);
                    }
                    break;

                case Advisor:
                    for (int i = 0; i < 4; i++)
                    {
                        int t = sq + Diag[i];
                        if (CanLand(t) && InPalace(t, _side)) Add(mv, ref n, sq, t);
                    }
                    break;

                case King:
                    for (int i = 0; i < 4; i++)
                    {
                        int t = sq + Ortho[i];
                        if (CanLand(t) && InPalace(t, _side)) Add(mv, ref n, sq, t);
                    }
                    break;

                case Pawn:
                    if (CanLand(sq + fwd)) Add(mv, ref n, sq, sq + fwd);   // 只能向前，到底線後前方為界外
                    if (Crossed(sq, _side))                                // 過河才能橫走，且永不後退
                    {
                        if (CanLand(sq - 1)) Add(mv, ref n, sq, sq - 1);
                        if (CanLand(sq + 1)) Add(mv, ref n, sq, sq + 1);
                    }
                    break;
            }
        }
        return n;
    }

    // ── 走子與還原 ────────────────────────────────────────────────────────────
    // 象棋沒有升變、沒有易位、沒有過路兵，所以還原資訊只有「被吃的是什麼」一項。

    private void Make(in Move m, out int captured)
    {
        captured = _b[m.To];
        int p = _b[m.From];
        _b[m.To] = p;
        _b[m.From] = 0;
        if ((p & 7) == King) _kingSq[(p & 8) >> 3] = m.To;   // 增量維護，省掉每節點掃盤找將
        _side ^= 8;
    }

    private void Unmake(in Move m, int captured)
    {
        _side ^= 8;
        int p = _b[m.To];
        _b[m.From] = p;
        _b[m.To] = captured;
        if ((p & 7) == King) _kingSq[(p & 8) >> 3] = m.From;
    }

    // ── 將軍偵測 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <paramref name="me"/> 方的將是否處於被吃的狀態（含白臉將）。走子後不成立才算合法。
    /// </summary>
    /// <remarks>
    /// 只針對「將」而寫，因此刻意略過士與象：士困在自家九宮、象困在自家半場，
    /// 兩者的活動範圍與對方九宮完全不相交，永遠碰不到對方的將。同理兩將也不可能貼在一起，
    /// 唯一的互相威脅就是同一直線照面（白臉將），併入直線掃描處理。
    /// </remarks>
    private bool KingInDanger(int me)
    {
        int k = _kingSq[me >> 3];
        int by = me ^ 8;
        int rook = Rook | by, cannon = Cannon | by, king = King | by;

        // 車、炮、白臉將：四個正交方向各掃一次。第一個子是車或（同欄的）將就被將軍；
        // 第一個子當砲架、其後的第一個子是炮，也被將軍。
        for (int d = 0; d < 4; d++)
        {
            int o = Ortho[d], t = k + o;
            while (_b[t] == 0) t += o;
            if (_b[t] == OFF) continue;
            if (_b[t] == rook) return true;
            if (_b[t] == king && (o == -S || o == S)) return true;   // 白臉將只論同一直線
            t += o;
            while (_b[t] == 0) t += o;
            if (_b[t] == cannon) return true;
        }

        // 馬：把「馬能走到將的位置」反過來看——馬在 k - HorseTo[i]，其蹩腿格仍相對於馬本身
        int horse = Horse | by;
        for (int i = 0; i < 8; i++)
        {
            int h = k - HorseTo[i];
            if (_b[h] == horse && _b[h + HorseLeg[i]] == 0) return true;
        }

        // 兵：正面一格；過河後另加左右一格
        int pawn = Pawn | by;
        if (_b[k + (by == Red ? S : -S)] == pawn) return true;   // 敵兵在將的後方朝將走來
        if (_b[k - 1] == pawn && Crossed(k - 1, by)) return true;
        if (_b[k + 1] == pawn && Crossed(k + 1, by)) return true;

        return false;
    }
}
