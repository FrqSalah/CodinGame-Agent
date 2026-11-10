// Bot de sortie de Silver — Mad Pod Racing, un pod par joueur.
// Il ne sert qu'à franchir le boss Silver : au-delà, la pipeline prend le relais en Gold.
// Entrée par tour : x y nextX nextY dist angle / oppX oppY. Sortie : x y poussée|BOOST|SHIELD.
#pragma GCC optimize("O2")
#include <bits/stdc++.h>
using namespace std;

struct Vec {
    double x = 0, y = 0;
};
static Vec operator+(Vec a, Vec b) { return {a.x + b.x, a.y + b.y}; }
static Vec operator-(Vec a, Vec b) { return {a.x - b.x, a.y - b.y}; }
static Vec operator*(Vec a, double k) { return {a.x * k, a.y * k}; }
static double norm(Vec a) { return hypot(a.x, a.y); }
static bool same(Vec a, Vec b) { return a.x == b.x && a.y == b.y; }
static double deg(double r) { return r * 180.0 / M_PI; }
static double wrap(double a) { return fmod(a + 540.0, 360.0) - 180.0; }

int main() {
    vector<Vec> cps;  // checkpoints, découverts pendant le premier tour de circuit
    bool circuitKnown = false, boostUsed = false, first = true;
    int longest = -1;  // checkpoint qui termine la plus longue ligne droite
    Vec prev, oppPrev;

    while (true) {
        int x, y, cx, cy, dist, angle, ox, oy;
        if (!(cin >> x >> y >> cx >> cy >> dist >> angle >> ox >> oy)) return 0;
        Vec me{(double)x, (double)y}, cp{(double)cx, (double)cy}, opp{(double)ox, (double)oy};

        if (!circuitKnown && (cps.empty() || !same(cps.back(), cp))) {
            if (cps.size() > 1 && same(cps[0], cp)) {
                circuitKnown = true;
                double best = -1;
                for (size_t i = 0; i < cps.size(); i++) {
                    double d = norm(cps[i] - cps[(i + cps.size() - 1) % cps.size()]);
                    if (d > best) best = d, longest = (int)i;
                }
            } else cps.push_back(cp);
        }

        Vec v = first ? Vec{} : (me - prev) * 0.85;   // vitesse au début de ce tour
        Vec vo = first ? Vec{} : (opp - oppPrev) * 0.85;
        prev = me, oppPrev = opp;

        // Orientation du pod, déduite de l'angle relatif donné par le jeu.
        double facing = deg(atan2(cp.y - me.y, cp.x - me.x)) - angle;
        auto turnTo = [&](Vec t) { return wrap(deg(atan2(t.y - me.y, t.x - me.x)) - facing); };

        // Viser le checkpoint en compensant la dérive ; enchaîner le suivant si l'élan suffit.
        Vec target = cp - v * 3.0;
        int idx = -1;
        if (circuitKnown)
            for (size_t i = 0; i < cps.size(); i++)
                if (same(cps[i], cp)) idx = (int)i;
        double speed = norm(v);
        if (idx >= 0 && dist < speed * 3.2 && fabs(angle) < 70)
            target = cps[(idx + 1) % cps.size()] - v * 3.0;

        double turn = first ? 0 : turnTo(target);
        double a = fabs(turn);
        int thrust = a >= 90 ? 0 : a < 20 ? 100 : (int)(100 * cos((a - 20) * M_PI / 140));
        if (target.x != cp.x - v.x * 3.0 || target.y != cp.y - v.y * 3.0) thrust = min(thrust, 60);  // virage anticipé
        thrust = clamp(thrust, 0, 100);

        string cmd = to_string(thrust);
        Vec meNext = me + v, oppNext = opp + vo;
        bool bigHit = !first && norm(meNext - oppNext) < 820 && norm(v - vo) > 500;
        bool goodBoost = !boostUsed && a < 4 && dist > 4000 && (first || (circuitKnown && idx == longest));
        if (bigHit) cmd = "SHIELD";
        else if (goodBoost) cmd = "BOOST", boostUsed = true;

        cout << (int)lround(target.x) << " " << (int)lround(target.y) << " " << cmd << endl;
        first = false;
    }
}
