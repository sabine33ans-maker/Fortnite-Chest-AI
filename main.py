
import os, re, json, time, hmac, hashlib, secrets, sqlite3, subprocess, shlex
from contextlib import contextmanager
from pathlib import Path
from datetime import datetime, timedelta, timezone, date
from zoneinfo import ZoneInfo
from typing import Optional
from fastapi import FastAPI, Request, UploadFile, File, Form, HTTPException, BackgroundTasks
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
from fastapi.middleware.trustedhost import TrustedHostMiddleware
from concurrent.futures import ThreadPoolExecutor, as_completed
import threading
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parent
DATA_DIR = Path(os.environ.get("DATA_DIR", ROOT / "data"))
UPLOAD_DIR = DATA_DIR / "uploads"
DB_PATH = DATA_DIR / "chest_ai.sqlite3"
MAX_UPLOAD = 100 * 1024 * 1024
ADMIN_CODE = os.environ.get("ADMIN_CODE", "")
DISCORD_CLIENT_ID = os.environ.get("DISCORD_CLIENT_ID", "").strip()
DISCORD_CLIENT_SECRET = os.environ.get("DISCORD_CLIENT_SECRET", "").strip()
DISCORD_REDIRECT_URI = os.environ.get("DISCORD_REDIRECT_URI", "").strip()
SESSION_SECRET = os.environ.get("SESSION_SECRET", "CHANGE-ME-BEFORE-PUBLISH-" + secrets.token_hex(24))
ANALYZER_COMMAND = os.environ.get("CHEST_ANALYZER_COMMAND", "dotnet /app/analyzer/ChestReplayCli.dll").strip()

DATA_DIR.mkdir(parents=True, exist_ok=True)
UPLOAD_DIR.mkdir(parents=True, exist_ok=True)

app = FastAPI(title="Fortnite Chest AI")
replay_jobs = {}
replay_db_lock = threading.Lock()

# One shared pool for the whole server: users share a small, bounded analyzer queue.
# Keep the default conservative for the Render Starter instance; can be tuned via env.
REPLAY_WORKERS = max(1, min(4, int(os.environ.get("REPLAY_WORKERS", "2"))))
replay_executor = ThreadPoolExecutor(max_workers=REPLAY_WORKERS)

app.mount("/static", StaticFiles(directory=ROOT / "static"), name="static")


@contextmanager
def db():
    # Give concurrent requests time to wait for short SQLite write transactions.
    con = sqlite3.connect(DB_PATH, timeout=30)
    con.row_factory = sqlite3.Row
    con.execute("PRAGMA foreign_keys=ON")
    con.execute("PRAGMA busy_timeout=30000")
    try:
        yield con
        con.commit()
    except Exception:
        con.rollback()
        raise
    finally:
        con.close()


def init_db():
    with db() as con:
        # WAL lets leaderboard/profile reads continue while replay results are written.
        con.execute("PRAGMA journal_mode=WAL")
        con.execute("PRAGMA synchronous=NORMAL")
        con.executescript("""
        CREATE TABLE IF NOT EXISTS users (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          pseudo TEXT NOT NULL COLLATE NOCASE UNIQUE,
          password_hash TEXT NOT NULL,
          salt TEXT NOT NULL,
          created_at TEXT NOT NULL,
          discord_id TEXT UNIQUE,
          epic_username TEXT NOT NULL DEFAULT '',
          legacy_chests INTEGER NOT NULL DEFAULT 0,
          legacy_matches INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS sessions (
          token_hash TEXT PRIMARY KEY,
          user_id INTEGER,
          role TEXT NOT NULL,
          expires_at INTEGER NOT NULL,
          FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS replays (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          user_id INTEGER NOT NULL,
          filename TEXT NOT NULL,
          stored_name TEXT NOT NULL,
          sha256 TEXT NOT NULL UNIQUE,
          match_id TEXT,
          chests INTEGER NOT NULL DEFAULT 0,
          status TEXT NOT NULL DEFAULT 'pending',
          note TEXT NOT NULL DEFAULT '',
          uploaded_at TEXT NOT NULL,
          FOREIGN KEY(user_id) REFERENCES users(id) ON DELETE CASCADE
        );
        CREATE TABLE IF NOT EXISTS legacy_profiles (
          pseudo TEXT PRIMARY KEY COLLATE NOCASE,
          chests INTEGER NOT NULL,
          matches INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS admin_login_attempts (
          ip TEXT NOT NULL,
          attempted_at INTEGER NOT NULL
        );
        """)

        columns = {
            row[1] for row in con.execute("PRAGMA table_info(users)").fetchall()
        }

        if "legacy_chests" not in columns:
            con.execute(
                "ALTER TABLE users ADD COLUMN legacy_chests INTEGER NOT NULL DEFAULT 0"
            )

        if "legacy_matches" not in columns:
            con.execute(
                "ALTER TABLE users ADD COLUMN legacy_matches INTEGER NOT NULL DEFAULT 0"
            )
        if "discord_id" not in columns:
            con.execute("ALTER TABLE users ADD COLUMN discord_id TEXT")
        if "epic_username" not in columns:
            con.execute("ALTER TABLE users ADD COLUMN epic_username TEXT NOT NULL DEFAULT ''")

        # Old SQLite databases do not inherit the UNIQUE constraint from CREATE TABLE.
        con.execute(
            "CREATE UNIQUE INDEX IF NOT EXISTS idx_users_discord_id "
            "ON users(discord_id) WHERE discord_id IS NOT NULL"
        )
        con.execute("UPDATE users SET epic_username=pseudo WHERE epic_username=''")

        con.execute(
            """INSERT OR IGNORE INTO legacy_profiles(pseudo,chests,matches)
               SELECT ?,?,? WHERE NOT EXISTS
               (SELECT 1 FROM users WHERE pseudo=? COLLATE NOCASE)""",
            ("Siko aura kingǃ", 121, 12, "Siko aura kingǃ")
        )


init_db()


# Sauvegardes SQLite automatiques : les profils, comptes, coffres et parties
# sont copiés sans interrompre les lectures/écritures du site.
BACKUP_DIR = DATA_DIR / "backups"
BACKUP_DIR.mkdir(parents=True, exist_ok=True)
BACKUP_INTERVAL_SECONDS = 6 * 60 * 60
BACKUP_KEEP_COUNT = 14


def create_database_backup():
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    destination = BACKUP_DIR / f"chest_ai_{stamp}.sqlite3"
    temporary = BACKUP_DIR / f".{destination.name}.tmp"

    try:
        with sqlite3.connect(DB_PATH, timeout=30) as source:
            with sqlite3.connect(temporary) as target:
                source.backup(target)
                target.execute("PRAGMA integrity_check")
        os.replace(temporary, destination)

        backups = sorted(
            BACKUP_DIR.glob("chest_ai_*.sqlite3"),
            key=lambda item: item.stat().st_mtime,
            reverse=True
        )
        for obsolete in backups[BACKUP_KEEP_COUNT:]:
            obsolete.unlink(missing_ok=True)

        print(f"SQLite backup created: {destination.name}", flush=True)
        return destination
    except Exception as exc:
        temporary.unlink(missing_ok=True)
        print(f"SQLite backup failed: {type(exc).__name__}: {str(exc)[:200]}", flush=True)
        return None


def automatic_backup_worker():
    while True:
        time.sleep(BACKUP_INTERVAL_SECONDS)
        create_database_backup()


# Une copie au démarrage, puis une copie toutes les 6 heures.
create_database_backup()
threading.Thread(
    target=automatic_backup_worker,
    name="sqlite-auto-backup",
    daemon=True
).start()


def now_iso():
    return datetime.now(timezone.utc).isoformat()


def hash_password(password: str, salt: Optional[str] = None):
    salt = salt or secrets.token_hex(16)
    digest = hashlib.pbkdf2_hmac(
        "sha256",
        password.encode(),
        bytes.fromhex(salt),
        240_000
    ).hex()
    return digest, salt


def new_session(user_id=None, role="viewer"):
    token = secrets.token_urlsafe(40)
    token_hash = hashlib.sha256(token.encode()).hexdigest()
    expires = int(time.time()) + 60 * 60 * 24 * 30

    with db() as con:
        con.execute(
            "INSERT INTO sessions(token_hash,user_id,role,expires_at) VALUES(?,?,?,?)",
            (token_hash, user_id, role, expires)
        )

    return token


def get_session(request: Request):
    token = request.cookies.get("chest_session")

    if not token:
        return None

    token_hash = hashlib.sha256(token.encode()).hexdigest()

    with db() as con:
        row = con.execute("""
            SELECT s.*, u.pseudo, u.epic_username FROM sessions s
            LEFT JOIN users u ON u.id=s.user_id
            WHERE s.token_hash=? AND s.expires_at>?
        """, (token_hash, int(time.time()))).fetchone()

    return dict(row) if row else None


def require_user(request: Request):
    s = get_session(request)

    if not s or s["role"] != "viewer" or not s["user_id"]:
        raise HTTPException(401, "Connecte-toi à ton compte.")

    return s


def require_admin(request: Request):
    s = get_session(request)

    if not s or s["role"] != "admin":
        raise HTTPException(403, "Accès administrateur requis.")

    return s


def safe_name(name):
    name = Path(name or "rediffusion.replay").name
    return re.sub(r"[^A-Za-z0-9._ -]", "_", name)[:120] or "rediffusion.replay"


def analyze_replay(path: Path):
    """
    Runs the bundled LED-N ChestCounter parser through the .NET CLI adapter.
    """

    if not ANALYZER_COMMAND:
        return None, "Analyseur .replay non configuré sur le serveur."

    try:
        command = shlex.split(
            ANALYZER_COMMAND,
            posix=(os.name != "nt")
        )

        if not command:
            return None, "Commande de l'analyseur vide."

        proc = subprocess.run(
            [*command, str(path)],
            capture_output=True,
            text=True,
            timeout=240,
            check=False
        )

        output_lines = [
            line for line in proc.stdout.splitlines() if line.strip()
        ]

        if output_lines:
            try:
                result = json.loads(output_lines[-1])
            except json.JSONDecodeError:
                result = None

            if isinstance(result, dict) and result.get("ok") is False:
                reason = str(result.get("error", "Rediffusion non analysable"))
                details = (proc.stderr or "").strip().splitlines()
                if details:
                    reason += " — " + details[-1][:200]
                return None, reason[:500]
        else:
            result = None

        if proc.returncode != 0:
            diagnostic = (proc.stderr or "").strip()
            if diagnostic:
                return None, diagnostic[-500:]
            return None, (
                f"Le décodeur s'est arrêté avec le code {proc.returncode} sans fournir de détail. "
                "Le fichier peut être incomplet ou utiliser un format de rediffusion non pris en charge."
            )

        if not isinstance(result, dict):
            return None, "Le décodeur n'a pas renvoyé de résultat valide."

        player = str(result.get("player_name", "")).strip()

        count = int(result.get("chests_opened", -1))

        if count < 0 or count > 500:
            return None, "Nombre de coffres invalide renvoyé par l'analyseur."

        match_id = str(result.get("match_id", "")).strip()

        if not match_id:
            return None, "L'analyseur n'a pas fourni d'identifiant de partie."

        raw_match_start = result.get("match_start_utc")

        if not raw_match_start:
            return None, "Date du match introuvable : rediffusion refusée par sécurité."

        try:
            match_start = datetime.fromisoformat(
                str(raw_match_start).replace("Z", "+00:00")
            )

            if match_start.tzinfo is None:
                match_start = match_start.replace(tzinfo=timezone.utc)

            match_date_fr = match_start.astimezone(
                ZoneInfo("Europe/Paris")
            ).date()

        except (TypeError, ValueError, OverflowError):
            return None, "Date du match invalide : rediffusion refusée."

        if match_date_fr < date(2026, 11, 1):
            return None, "Rediffusion refusée : seules les parties jouées à partir du 01/11/2026 sont acceptées."

        return {
            "chests": count,
            "match_id": match_id,
            "match_date": match_date_fr.isoformat()
        }, ""

    except subprocess.TimeoutExpired:
        return None, "L'analyse de la rediffusion a dépassé le délai autorisé."

    except Exception as exc:
        return None, f"Analyse impossible : {type(exc).__name__}"


@app.get("/", response_class=HTMLResponse)
def home():
    return (
        ROOT / "static" / "index.html"
    ).read_text(encoding="utf-8")


@app.get("/api/me")
def me(request: Request):
    s = get_session(request)

    if not s:
        return {"logged_in": False, "admin": False}

    return {
        "logged_in": True,
        "admin": s["role"] == "admin",
        "pseudo": s.get("pseudo") or "Admin",
        "epic_username": s.get("epic_username") or "",
        "role": s["role"]
    }


@app.post("/api/register")
def register(pseudo: str = Form(...), password: str = Form(...)):
    pseudo = pseudo.strip()

    if not 2 <= len(pseudo) <= 32 or any(ord(ch) < 32 for ch in pseudo):
        raise HTTPException(
            400,
            "Pseudo invalide (2 à 32 caractères, sans caractères de contrôle)."
        )

    if len(password) < 8:
        raise HTTPException(400, "Mot de passe : 8 caractères minimum.")

    with db() as con:
        if con.execute(
            "SELECT 1 FROM users WHERE pseudo=? COLLATE NOCASE",
            (pseudo,)
        ).fetchone():
            raise HTTPException(409, "Ce pseudo est déjà utilisé.")

        legacy = con.execute(
            "SELECT chests,matches FROM legacy_profiles WHERE pseudo=? COLLATE NOCASE",
            (pseudo,)
        ).fetchone()

        pw_hash, salt = hash_password(password)

        cur = con.execute(
            """INSERT INTO users
            (pseudo,password_hash,salt,created_at,legacy_chests,legacy_matches,epic_username)
            VALUES(?,?,?,?,?,?,?)""",
            (
                pseudo,
                pw_hash,
                salt,
                now_iso(),
                legacy["chests"] if legacy else 0,
                legacy["matches"] if legacy else 0,
                pseudo
            )
        )

        uid = cur.lastrowid

        if legacy:
            con.execute(
                "DELETE FROM legacy_profiles WHERE pseudo=? COLLATE NOCASE",
                (pseudo,)
            )

    token = new_session(uid, "viewer")
    response = JSONResponse({"ok": True, "pseudo": pseudo})

    response.set_cookie(
        "chest_session",
        token,
        httponly=True,
        secure=os.environ.get("COOKIE_SECURE", "0") == "1",
        samesite="lax",
        max_age=60 * 60 * 24 * 30,
        path="/"
    )

    return response


@app.post("/api/login")
def login(pseudo: str = Form(...), password: str = Form(...)):
    with db() as con:
        row = con.execute(
            "SELECT * FROM users WHERE pseudo=? COLLATE NOCASE",
            (pseudo.strip(),)
        ).fetchone()

    if not row:
        raise HTTPException(401, "Pseudo ou mot de passe incorrect.")

    candidate, _ = hash_password(password, row["salt"])

    if not hmac.compare_digest(candidate, row["password_hash"]):
        raise HTTPException(401, "Pseudo ou mot de passe incorrect.")

    token = new_session(row["id"], "viewer")
    response = JSONResponse({"ok": True, "pseudo": row["pseudo"]})

    response.set_cookie(
        "chest_session",
        token,
        httponly=True,
        secure=os.environ.get("COOKIE_SECURE", "0") == "1",
        samesite="lax",
        max_age=60 * 60 * 24 * 30,
        path="/"
    )

    return response



@app.get("/api/discord/start")
def discord_start(request: Request):
    if not (DISCORD_CLIENT_ID and DISCORD_CLIENT_SECRET and DISCORD_REDIRECT_URI):
        raise HTTPException(503, "Connexion Discord non configurée sur Render.")
    state = secrets.token_urlsafe(32)
    params = urllib.parse.urlencode({"client_id": DISCORD_CLIENT_ID, "redirect_uri": DISCORD_REDIRECT_URI, "response_type": "code", "scope": "identify", "state": state})
    response = RedirectResponse("https://discord.com/oauth2/authorize?" + params, status_code=302)
    response.set_cookie("discord_oauth_state", state, httponly=True, secure=True, samesite="lax", max_age=600, path="/")
    return response


@app.get("/api/discord/callback")
def discord_callback(request: Request, code: str = "", state: str = "", error: str = ""):
    expected = request.cookies.get("discord_oauth_state", "")
    if error:
        return RedirectResponse("/", status_code=303)
    if not code or not state or not expected or not hmac.compare_digest(state, expected):
        raise HTTPException(400, "Vérification Discord invalide. Réessaie.")
    if not (DISCORD_CLIENT_ID and DISCORD_CLIENT_SECRET and DISCORD_REDIRECT_URI):
        raise HTTPException(503, "Connexion Discord non configurée sur Render.")
    body = urllib.parse.urlencode({"client_id": DISCORD_CLIENT_ID, "client_secret": DISCORD_CLIENT_SECRET, "grant_type": "authorization_code", "code": code, "redirect_uri": DISCORD_REDIRECT_URI}).encode()
    user_agent = "DiscordBot (https://github.com/sabine33ans-maker/Fortnite-Chest-AI, 1.0)"
    stage = "token"
    req = urllib.request.Request(
        "https://discord.com/api/v10/oauth2/token",
        data=body,
        headers={
            "Content-Type": "application/x-www-form-urlencoded",
            "User-Agent": user_agent,
        },
    )
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            token_data = json.loads(r.read().decode())
        stage = "profile"
        req = urllib.request.Request(
            "https://discord.com/api/v10/users/@me",
            headers={
                "Authorization": "Bearer " + token_data["access_token"],
                "User-Agent": user_agent,
            },
        )
        with urllib.request.urlopen(req, timeout=15) as r:
            profile = json.loads(r.read().decode())
    except Exception as exc:
        # Never log OAuth codes, client secrets, or access tokens.
        print(
            f"Discord OAuth {stage} request failed: {type(exc).__name__}: {str(exc)[:300]}",
            flush=True
        )
        if isinstance(exc, urllib.error.HTTPError):
            try:
                upstream_detail = exc.read().decode("utf-8", errors="replace")[:500]
                print(f"Discord upstream response: {upstream_detail}", flush=True)
            except Exception:
                pass
        raise HTTPException(502, "Discord n'a pas pu confirmer ton compte.")
    discord_id = str(profile.get("id", ""))
    if not discord_id:
        raise HTTPException(502, "Identifiant Discord introuvable.")
    current = get_session(request)
    with db() as con:
        found = con.execute("SELECT id FROM users WHERE discord_id=?", (discord_id,)).fetchone()
        if current and current.get("role") == "viewer" and current.get("user_id"):
            if found and found["id"] != current["user_id"]:
                raise HTTPException(409, "Ce compte Discord est déjà lié à un autre compte.")
            con.execute("UPDATE users SET discord_id=? WHERE id=?", (discord_id, current["user_id"]))
            uid = current["user_id"]
        elif found:
            uid = found["id"]
        else:
            response = RedirectResponse("/?discord=link_required", status_code=303)
            response.delete_cookie("discord_oauth_state", path="/")
            return response
    token = new_session(uid, "viewer")
    response = RedirectResponse("/", status_code=303)
    response.delete_cookie("discord_oauth_state", path="/")
    response.set_cookie("chest_session", token, httponly=True, secure=os.environ.get("COOKIE_SECURE", "0") == "1", samesite="lax", max_age=2592000, path="/")
    return response


@app.post("/api/profile/epic")
def set_epic_username(request: Request, epic_username: str = Form(...)):
    s = require_user(request)
    epic_username = epic_username.strip()
    if not 2 <= len(epic_username) <= 32 or any(ord(c) < 32 for c in epic_username):
        raise HTTPException(400, "Pseudo Epic invalide (2 à 32 caractères).")
    with db() as con:
        con.execute("UPDATE users SET epic_username=? WHERE id=?", (epic_username, s["user_id"]))
    return {"ok": True, "epic_username": epic_username}

@app.post("/api/admin/login")
def admin_login(request: Request, code: str = Form(...)):
    ip = request.client.host if request.client else "unknown"
    cutoff = int(time.time()) - 15 * 60

    with db() as con:
        con.execute(
            "DELETE FROM admin_login_attempts WHERE attempted_at<?",
            (cutoff,)
        )

        attempts = con.execute(
            "SELECT COUNT(*) FROM admin_login_attempts WHERE ip=?",
            (ip,)
        ).fetchone()[0]

    if attempts >= 5:
        raise HTTPException(429, "Trop de tentatives. Réessaie dans 15 minutes.")

    if not ADMIN_CODE or not hmac.compare_digest(code, ADMIN_CODE):
        with db() as con:
            con.execute(
                "INSERT INTO admin_login_attempts(ip,attempted_at) VALUES(?,?)",
                (ip, int(time.time()))
            )

        raise HTTPException(401, "Code admin incorrect.")

    with db() as con:
        con.execute("DELETE FROM admin_login_attempts WHERE ip=?", (ip,))

    token = new_session(None, "admin")
    response = JSONResponse({"ok": True})

    response.set_cookie(
        "chest_session",
        token,
        httponly=True,
        secure=os.environ.get("COOKIE_SECURE", "0") == "1",
        samesite="lax",
        max_age=60 * 60 * 24 * 30,
        path="/"
    )

    return response


@app.post("/api/logout")
def logout(request: Request):
    token = request.cookies.get("chest_session")

    if token:
        with db() as con:
            con.execute(
                "DELETE FROM sessions WHERE token_hash=?",
                (hashlib.sha256(token.encode()).hexdigest(),)
            )

    response = JSONResponse({"ok": True})
    response.delete_cookie("chest_session", path="/")

    return response


@app.get("/api/leaderboard")
def leaderboard():
    with db() as con:
        users = con.execute("""
            SELECT u.id,u.pseudo,
                   u.legacy_chests + COALESCE(SUM(CASE WHEN r.status='analyzed' THEN r.chests ELSE 0 END),0) AS chests,
                   u.legacy_matches + COALESCE(SUM(CASE WHEN r.status='analyzed' THEN 1 ELSE 0 END),0) AS matches
            FROM users u LEFT JOIN replays r ON r.user_id=u.id
            GROUP BY u.id,u.pseudo,u.legacy_chests,u.legacy_matches
        """).fetchall()

        legacy = con.execute(
            "SELECT pseudo,chests,matches FROM legacy_profiles"
        ).fetchall()

    rows = [
        {
            "pseudo": x["pseudo"],
            "chests": x["chests"],
            "matches": x["matches"] or 0,
            "legacy": False
        }
        for x in users
    ]

    rows += [
        {
            "pseudo": x["pseudo"],
            "chests": x["chests"],
            "matches": x["matches"],
            "legacy": True
        }
        for x in legacy
    ]

    rows.sort(key=lambda x: (-x["chests"], x["pseudo"].casefold()))

    return {
        "players": rows,
        "total_players": len(rows)
    }


@app.get("/api/my/replays")
def my_replays(request: Request):
    s = require_user(request)

    with db() as con:
        rows = con.execute(
            """SELECT id,filename,sha256,chests,status,note,uploaded_at
            FROM replays WHERE user_id=? ORDER BY id DESC""",
            (s["user_id"],)
        ).fetchall()

    return {"replays": [dict(r) for r in rows]}


def process_one_replay(item, user_id):
    result_item = {
        "filename": item["filename"],
        "ok": False,
        "error": ""
    }

    path = Path(item["path"])

    try:
        if item.get("error"):
            raise ValueError(item["error"])

        with replay_db_lock:
            with db() as con:
                duplicate = con.execute(
                    "SELECT id FROM replays WHERE sha256=?",
                    (item["sha256"],)
                ).fetchone()

        if duplicate:
            raise ValueError("Cette rediffusion a déjà été envoyée.")

        result, note = analyze_replay(path)

        if not result:
            raise ValueError(note or "Rediffusion non analysable.")

        with replay_db_lock:
            with db() as con:
                duplicate = con.execute(
                    "SELECT id FROM replays WHERE sha256=?",
                    (item["sha256"],)
                ).fetchone()

                duplicate_match = con.execute(
                    """SELECT id FROM replays
                    WHERE match_id=? AND status='analyzed'""",
                    (result["match_id"],)
                ).fetchone()

                if duplicate:
                    raise ValueError("Cette rediffusion a déjà été envoyée.")

                if duplicate_match:
                    raise ValueError("Cette partie a déjà été comptabilisée.")

                con.execute(
                    """INSERT INTO replays
                    (user_id,filename,stored_name,sha256,match_id,chests,status,note,uploaded_at)
                    VALUES(?,?,?,?,?,?,?,?,?)""",
                    (
                        user_id,
                        item["filename"],
                        item["stored_name"],
                        item["sha256"],
                        result["match_id"],
                        result["chests"],
                        "analyzed",
                        "",
                        now_iso()
                    )
                )

        result_item.update({
            "ok": True,
            "chests": result["chests"],
            "match_date": result["match_date"]
        })

    except (ValueError, sqlite3.IntegrityError) as exc:
        result_item["error"] = str(exc)

    except Exception as exc:
        result_item["error"] = f"Erreur d'analyse : {type(exc).__name__}"

    finally:
        path.unlink(missing_ok=True)

    return result_item


def process_replay_batch(job_id, user_id, files):
    job = replay_jobs[job_id]
    futures = {
        replay_executor.submit(process_one_replay, item, user_id): item
        for item in files
    }

    try:
        for future in as_completed(futures):
            item = futures[future]
            try:
                result_item = future.result()
            except Exception as exc:
                result_item = {
                    "filename": item.get("filename", "rediffusion.replay"),
                    "ok": False,
                    "error": f"Erreur interne d'analyse : {type(exc).__name__}"
                }

            with replay_db_lock:
                job["results"].append(result_item)
                job["completed"] += 1
                job["percent"] = round(
                    job["completed"] / job["total"] * 100
                )

    finally:
        with replay_db_lock:
            job["finished"] = True
            job["percent"] = 100
            job["accepted"] = sum(
                1 for r in job["results"] if r.get("ok")
            )
            job["rejected"] = job["total"] - job["accepted"]


@app.post("/api/replays")
async def upload_replay(
    background_tasks: BackgroundTasks,
    request: Request,
    replay: list[UploadFile] = File(...)
):
    s = require_user(request)
    if not replay:
        raise HTTPException(400, "Choisis au moins une rediffusion.")

    if len(replay) > 100:
        raise HTTPException(400, "100 rediffusions maximum par envoi.")

    files = []

    for uploaded in replay:
        filename = safe_name(uploaded.filename)
        stored = f"{secrets.token_hex(16)}.replay"
        path = UPLOAD_DIR / stored
        error = ""

        try:
            if not filename.lower().endswith(".replay"):
                error = "Format refusé : importe un fichier .replay."
                content = b""
            else:
                content = await uploaded.read(MAX_UPLOAD + 1)

                if len(content) > MAX_UPLOAD:
                    error = "Fichier trop volumineux (100 Mo maximum)."

                elif len(content) < 64 or content[:4] != bytes.fromhex("7fe2a21c"):
                    error = "Fichier Fortnite invalide."

            digest = hashlib.sha256(content).hexdigest()

            if not error:
                path.write_bytes(content)

            files.append({
                "filename": filename,
                "stored_name": stored,
                "path": str(path),
                "sha256": digest,
                "error": error
            })

        finally:
            await uploaded.close()

    job_id = secrets.token_urlsafe(16)

    replay_jobs[job_id] = {
        "user_id": s["user_id"],
        "total": len(files),
        "completed": 0,
        "percent": 0,
        "accepted": 0,
        "rejected": 0,
        "finished": False,
        "results": []
    }

    background_tasks.add_task(
        process_replay_batch,
        job_id,
        s["user_id"],
        files
    )

    return {
        "ok": True,
        "job_id": job_id,
        "total": len(files)
    }


@app.get("/api/replays/progress/{job_id}")
def replay_progress(job_id: str, request: Request):
    s = require_user(request)

    with replay_db_lock:
        job = replay_jobs.get(job_id)
        if not job or job["user_id"] != s["user_id"]:
            raise HTTPException(404, "Analyse introuvable.")
        snapshot = {
            "total": job["total"],
            "completed": job["completed"],
            "percent": job["percent"],
            "finished": job["finished"],
            "accepted": job["accepted"],
            "rejected": job["rejected"],
            "results": list(job["results"])
        }

    return snapshot


@app.delete("/api/replays/{replay_id}")
def delete_my_replay(replay_id: int, request: Request):
    s = require_user(request)

    with db() as con:
        row = con.execute(
            "SELECT stored_name FROM replays WHERE id=? AND user_id=?",
            (replay_id, s["user_id"])
        ).fetchone()

        if not row:
            raise HTTPException(
                404,
                "Rediffusion introuvable dans ton compte."
            )

        con.execute(
            "DELETE FROM replays WHERE id=? AND user_id=?",
            (replay_id, s["user_id"])
        )

    (UPLOAD_DIR / row["stored_name"]).unlink(missing_ok=True)

    return {"ok": True}


@app.get("/api/admin/replays")
def admin_replays(request: Request):
    require_admin(request)

    with db() as con:
        rows = con.execute("""
            SELECT r.id,r.filename,r.sha256,r.chests,r.status,r.note,r.uploaded_at,u.pseudo
            FROM replays r JOIN users u ON u.id=r.user_id
            ORDER BY r.id DESC LIMIT 500
        """).fetchall()

    return {"replays": [dict(r) for r in rows]}


@app.delete("/api/admin/replays/{replay_id}")
def admin_delete_replay(replay_id: int, request: Request):
    require_admin(request)

    with db() as con:
        row = con.execute(
            "SELECT stored_name FROM replays WHERE id=?",
            (replay_id,)
        ).fetchone()

        if not row:
            raise HTTPException(404, "Rediffusion introuvable.")

        con.execute(
            "DELETE FROM replays WHERE id=?",
            (replay_id,)
        )

    (UPLOAD_DIR / row["stored_name"]).unlink(missing_ok=True)

    return {"ok": True}


@app.get("/api/admin/users")
def admin_users(request: Request):
    require_admin(request)

    with db() as con:
        rows = con.execute("""
            SELECT u.id,u.pseudo,u.created_at,
            u.legacy_matches + COUNT(CASE WHEN r.status='analyzed' THEN 1 END) AS matches,
            u.legacy_chests + COALESCE(SUM(CASE WHEN r.status='analyzed' THEN r.chests ELSE 0 END),0) AS chests
            FROM users u LEFT JOIN replays r ON r.user_id=u.id
            GROUP BY u.id,u.legacy_matches,u.legacy_chests
            ORDER BY u.pseudo
        """).fetchall()

    return {"users": [dict(r) for r in rows]}


@app.delete("/api/admin/users/{user_id}")
def admin_delete_user(user_id: int, request: Request):
    require_admin(request)

    with db() as con:
        rows = con.execute(
            "SELECT stored_name FROM replays WHERE user_id=?",
            (user_id,)
        ).fetchall()

        con.execute(
            "DELETE FROM users WHERE id=?",
            (user_id,)
        )

    for row in rows:
        (UPLOAD_DIR / row["stored_name"]).unlink(missing_ok=True)

    return {"ok": True}


@app.get("/health")
def health():
    return {
        "ok": True,
        "analyzer_configured": bool(ANALYZER_COMMAND)
    }
