// Bot de départ — Mad Pod Racing, ligues Gold et Legend (deux pods par joueur).
// Pod 0 : coureur. Pod 1 : bloqueur, qui se place devant le meilleur pod adverse.
// Point de départ volontairement simple : la boucle d'amélioration le fera évoluer.
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

struct Pod {
    Vec p, v;
    double angle = -1;  // degrés ; -1 au premier tour
    int next = 1;
    int passed = 0;     // checkpoints franchis depuis le départ
};

static int laps, cpCount;
static vector<Vec> cps;

// Écart signé, en degrés, entre l'orientation du pod et la direction d'un point.
static double turnNeeded(const Pod& pod, Vec target) {
    if (pod.angle < 0) return 0;  // premier tour : rotation libre
    double want = atan2(target.y - pod.p.y, target.x - pod.p.x) * 180.0 / M_PI;
    double d = fmod(want - pod.angle + 540.0, 360.0) - 180.0;
    return d;
}

static double progress(const Pod& pod) {
    return pod.passed * 100000.0 - norm(cps[pod.next] - pod.p);
}

// Les deux pods se toucheront-ils au prochain tour, et assez fort pour qu'un bouclier serve ?
static bool hardHit(const Pod& a, const Pod& b, double minSpeed) {
    Vec na = a.p + a.v, nb = b.p + b.v;
    return norm(na - nb) < 850 && norm(a.v - b.v) > minSpeed;
}

static int thrustFor(double turn, double distToTarget) {
    double a = fabs(turn);
    if (a >= 90) return 0;
    double t = a < 30 ? 100 : 100 * (90 - a) / 60;
    if (distToTarget < 1200) t *= max(0.5, distToTarget / 1200);
    return clamp((int)t, 0, 100);
}

int main() {
    cin >> laps >> cpCount;
    cps.resize(cpCount);
    for (auto& c : cps) cin >> c.x >> c.y;

    Pod mine[2], theirs[2];
    bool boostUsed = false;
    int turn = 0;

    while (true) {
        for (int i = 0; i < 4; i++) {
            Pod& pod = i < 2 ? mine[i] : theirs[i - 2];
            int x, y, vx, vy, angle, next;
            if (!(cin >> x >> y >> vx >> vy >> angle >> next)) return 0;
            if (turn > 0 && next != pod.next) pod.passed++;
            pod.p = {(double)x, (double)y};
            pod.v = {(double)vx, (double)vy};
            pod.angle = angle;
            pod.next = next;
        }

        // --- Coureur ---
        Pod& run = mine[0];
        Vec cp = cps[run.next];
        double dist = norm(cp - run.p);
        Vec target = cp - run.v * 3.0;
        // Arrivée lancée : on vise déjà le checkpoint suivant pour enchaîner le virage.
        if (dist < norm(run.v) * 3.0) target = cps[(run.next + 1) % cpCount] - run.v * 3.0;
        double t = turnNeeded(run, target);
        string runCmd;
        bool hit = hardHit(run, theirs[0], 450) || hardHit(run, theirs[1], 450);
        if (turn > 0 && hit) runCmd = "SHIELD";
        else if (!boostUsed && turn > 0 && fabs(t) < 5 && dist > 5000) {
            runCmd = "BOOST";
            boostUsed = true;
        } else runCmd = to_string(thrustFor(t, dist));
        cout << (int)target.x << " " << (int)target.y << " " << runCmd << endl;

        // --- Bloqueur : se placer sur le chemin du meilleur pod adverse ---
        Pod& block = mine[1];
        const Pod& leader = progress(theirs[0]) >= progress(theirs[1]) ? theirs[0] : theirs[1];
        Vec aim;
        if (norm(leader.p - block.p) < 2000) aim = leader.p + leader.v * 2.0;  // intercepter
        else aim = cps[leader.next];                                            // l'attendre
        Vec btarget = aim - block.v * 2.0;
        double bt = turnNeeded(block, btarget);
        string blockCmd;
        if (turn > 0 && hardHit(block, leader, 350)) blockCmd = "SHIELD";
        else {
            int th = thrustFor(bt, 5000);
            if (norm(cps[leader.next] - block.p) < 800 && norm(leader.p - block.p) > 3000) th = 0;  // déjà en place
            blockCmd = to_string(th);
        }
        cout << (int)btarget.x << " " << (int)btarget.y << " " << blockCmd << endl;

        turn++;
    }
}
