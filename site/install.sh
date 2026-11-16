#!/bin/sh
# CodeAlta installer for macOS and Linux: https://codealta.io
#
#   curl -fsSL https://codealta.io/install.sh | sh
#
# It installs what CodeAlta needs and CodeAlta itself, for the current user:
#   1. the .NET 10 SDK, when no suitable one is found: in ~/.dotnet with Microsoft's dotnet-install script, without
#      administrator rights. A .NET of the system that is too old is updated where it is, which asks for them;
#   2. on Linux, what the window of the desktop app needs: GTK 4 and WebKitGTK 6.0, with the package manager of the
#      system, and on Ubuntu 24.04 or later an AppArmor profile, as the browsers have. Both ask for administrator rights;
#   3. CodeAlta, a .NET tool of nuget.org (dotnet tool update -g CodeAlta);
#   4. ~/.dotnet/tools on the PATH of your shell, when it is not there yet.
# A .NET 10 that is already there, of the system or of the user, is used as it is. Run the script again to update CodeAlta.
#
# Options, after `sh -s --`:
#   --desktop       CodeAlta Desktop (alta): the default where a desktop session is found
#   --tui           CodeAlta TUI (altatui), the terminal app
#   --cli           CodeAlta CLI (altacli), for scripts, CI and editors
#   --all           the three of them
#   --prerelease    the latest preview instead of the latest release
#   --version <v>   that version of CodeAlta
#   --no-modify-path  leave the profile of your shell as it is
#   --help          this text
#
# Windows: irm https://codealta.io/install.ps1 | iex
#
# The whole script is in functions run by its last line, so that a download cut short runs nothing.
# sh has no variables of a function: `local`, which dash, ash, bash and zsh all have, keeps them apart.

set -u

# The only place CodeAlta is taken from, whatever feeds the user has: CODEALTA_NUGET_SOURCE names a mirror of nuget.org.
NUGET_SOURCE="${CODEALTA_NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"
DOTNET_INSTALL_URL="https://dot.net/v1/dotnet-install.sh"
DOTNET_DOWNLOAD_PAGE="https://dotnet.microsoft.com/download/dotnet/10.0"
DOTNET_CHANNEL="10.0"
# CodeAlta Desktop crashes after hours on an older .NET 10 on macOS.
MAC_MINIMUM_PATCH=13
GUIDE="https://codealta.io/docs/getting-started/"
TROUBLESHOOTING="https://codealta.io/docs/troubleshooting/"

# --- Output -------------------------------------------------------------------------------------------------------------

setup_style() {
    TTY=0
    COLOR=none
    if [ -t 1 ]; then
        TTY=1
        if [ -z "${NO_COLOR:-}" ] && [ "${TERM:-dumb}" != "dumb" ]; then
            case "${COLORTERM:-}" in
                truecolor | 24bit) COLOR=rgb ;;
                *) COLOR=256 ;;
            esac
        fi
    fi
    UTF8=0
    case "${LC_ALL:-${LC_CTYPE:-${LANG:-}}}" in
        *[Uu][Tt][Ff]-8* | *[Uu][Tt][Ff]8*) UTF8=1 ;;
        # The terminals of macOS are UTF-8, also where no locale is set (a command run over SSH).
        '') [ "$(uname -s)" != Darwin ] || UTF8=1 ;;
    esac
    ESC=$(printf '\033')
    if [ "$COLOR" = none ]; then
        C_RESET="" C_BOLD="" C_DIM="" C_OK="" C_WARN="" C_ERR="" C_ACCENT="" C_CODE=""
    else
        C_RESET="${ESC}[0m" C_BOLD="${ESC}[1m" C_DIM="${ESC}[2m"
        C_OK="${ESC}[38;5;78m" C_WARN="${ESC}[38;5;221m" C_ERR="${ESC}[38;5;203m"
        C_ACCENT="${ESC}[38;5;45m" C_CODE="${ESC}[38;5;255m"
        if [ "$COLOR" = rgb ]; then
            C_ACCENT="${ESC}[38;2;0;209;255m" C_CODE="${ESC}[38;2;234;242;255m"
        fi
    fi
    if [ "$UTF8" = 1 ]; then
        S_OK="✓" S_WARN="!" S_ERR="✗" S_DOT="·" S_ARROW="→"
        FRAMES="⠋ ⠙ ⠹ ⠸ ⠼ ⠴ ⠦ ⠧ ⠇ ⠏"
    else
        S_OK="ok" S_WARN="!" S_ERR="x" S_DOT="-" S_ARROW="->"
        FRAMES="| / - \\"
    fi
}

# The logo of the website: "Code" in its soft white, "Alta" in the gradient of the brand (cyan, indigo, purple),
# one color for each column. Drawn here with # for a full block and : for a light shade.
banner() {
    local columns
    columns=$( (tput cols) 2>/dev/null || echo 80)
    case "$columns" in '' | *[!0-9]*) columns=80 ;; esac
    printf '\n'
    if [ "$UTF8" = 1 ] && [ "$columns" -ge 78 ]; then
        awk -v mode="$COLOR" -v full="█" -v shade="░" '
            function mix(a, b, t) { return int(a + (b - a) * t + 0.5) }
            BEGIN { cut = 40; span = 34; palette[1] = 45; palette[2] = 39; palette[3] = 63; palette[4] = 99; palette[5] = 135 }
            {
                out = ""
                for (i = 1; i <= length($0); i++) {
                    c = substr($0, i, 1)
                    if (c == " ") { out = out " "; continue }
                    glyph = (c == "#") ? full : shade
                    if (mode == "none") { out = out glyph; continue }
                    if (i <= cut) color = (mode == "rgb") ? "38;2;234;242;255" : "38;5;255"
                    else {
                        t = (i - cut - 1) / span
                        if (t > 1) t = 1
                        if (mode != "rgb") color = "38;5;" palette[int(t * 4.999) + 1]
                        else if (t < 0.55) color = "38;2;" mix(0, 79, t / 0.55) ";" mix(209, 70, t / 0.55) ";" mix(255, 229, t / 0.55)
                        else color = "38;2;" mix(79, 168, (t - 0.55) / 0.45) ";" mix(70, 85, (t - 0.55) / 0.45) ";" mix(229, 247, (t - 0.55) / 0.45)
                    }
                    out = out "\033[" color "m" glyph
                }
                print "  " out (mode == "none" ? "" : "\033[0m")
            }' <<'LOGO'
   ######                  ##                ##       ##    ##
  ##::::##                :##               ####     :##   :##
 ##    ::    ######       :##   #####      ##::##    :##  ######   ######
:##         ##::::##   ######  ##:::##    ##  ::##   :## :::##:   ::::::##
:##        :##   :##  ##:::## :#######   ##########  :##   :##     #######
::##    ## :##   :## :##  :## :##::::   :##::::::##  :##   :##    ##::::##
 ::######  ::######  ::###### ::######  :##     :##  ###   ::##  ::########
  ::::::    ::::::    ::::::   ::::::   ::      ::  :::     ::    ::::::::
LOGO
    else
        printf '  %s%sCode%s%sAlta%s\n' "$C_BOLD" "$C_CODE" "$C_RESET$C_BOLD" "$C_ACCENT" "$C_RESET"
    fi
    printf '\n  %sYour agents. One workspace. One fleet.%s\n\n' "$C_DIM" "$C_RESET"
}

ok() { printf '  %s%s%s %s\n' "$C_OK" "$S_OK" "$C_RESET" "$1"; }
info() { printf '  %s%s %s%s\n' "$C_DIM" "$S_DOT" "$1" "$C_RESET"; }
warn() { printf '  %s%s%s %s\n' "$C_WARN" "$S_WARN" "$C_RESET" "$1"; }
detail() { printf '    %s%s%s\n' "$C_DIM" "$1" "$C_RESET"; }

die() {
    local line
    printf '\n  %s%s %s%s\n' "$C_ERR" "$S_ERR" "$1" "$C_RESET" >&2
    shift
    for line in "$@"; do printf '    %s\n' "$line" >&2; done
    printf '\n' >&2
    exit 1
}

# Shows the end of the output of the command that just failed.
show_log() {
    [ -s "$LOG" ] || return 0
    tail -n 15 "$LOG" | while IFS= read -r line; do printf '    %s%s%s\n' "$C_DIM" "$line" "$C_RESET" >&2; done
}

# Runs a command with its output kept in $LOG, behind a spinner on a terminal. Returns the status of the command.
run_quiet() {
    local label frame status glyph
    label=$1
    shift
    if [ "$TTY" = 0 ]; then
        info "$label"
        "$@" > "$LOG" 2>&1 < /dev/null
        return $?
    fi
    "$@" > "$LOG" 2>&1 < /dev/null &
    CHILD=$!
    frame=0
    while kill -0 "$CHILD" 2> /dev/null; do
        frame=$((frame + 1))
        set -- $FRAMES
        [ "$frame" -le $# ] || frame=1
        eval "glyph=\${$frame}"
        printf '\r  %s%s%s %s' "$C_ACCENT" "$glyph" "$C_RESET" "$label"
        sleep 0.1 2> /dev/null || sleep 1
    done
    wait "$CHILD"
    status=$?
    CHILD=""
    printf '\r%s[2K' "$ESC"
    return $status
}

cleanup() {
    [ -z "${CHILD:-}" ] || kill "$CHILD" 2> /dev/null
    [ -z "${WORK:-}" ] || { cd / && rm -rf "$WORK"; }
}

# --- System -------------------------------------------------------------------------------------------------------------

has() { command -v "$1" > /dev/null 2>&1; }

download() {
    if has curl; then
        curl -fsSL --retry 3 --retry-delay 1 -o "$2" "$1"
    else
        wget -q -O "$2" "$1"
    fi
}

# The file a path names, links followed.
resolve() {
    local target count link directory
    target=$1
    count=0
    while [ -L "$target" ] && [ "$count" -lt 20 ]; do
        link=$(readlink "$target") || break
        case "$link" in
            /*) target=$link ;;
            *) target=$(dirname "$target")/$link ;;
        esac
        count=$((count + 1))
    done
    directory=$(cd "$(dirname "$target")" 2> /dev/null && pwd -P) || directory=$(dirname "$target")
    printf '%s/%s\n' "$directory" "$(basename "$target")"
}

# A folder as the system names it, links followed; as given when it does not exist.
physical() {
    (cd "$1" 2> /dev/null && pwd -P) || printf '%s\n' "$1"
}

# Whether a shared library of that name is installed (Linux).
has_lib() {
    local tool directory
    for tool in ldconfig /sbin/ldconfig /usr/sbin/ldconfig; do
        if has "$tool" && "$tool" -p 2> /dev/null | grep -q "$1"; then return 0; fi
    done
    for directory in /usr/lib /usr/lib64 /lib /lib64 /usr/local/lib /usr/lib/*-linux-*; do
        if ls "$directory/$1"* > /dev/null 2>&1; then return 0; fi
    done
    return 1
}

detect_platform() {
    local candidate
    case "$(uname -s)" in
        Darwin) OS=macos ;;
        Linux) OS=linux ;;
        MINGW* | MSYS* | CYGWIN*)
            die "This script is for macOS and Linux." "On Windows, run in PowerShell:  irm https://codealta.io/install.ps1 | iex"
            ;;
        *) die "CodeAlta runs on Windows, macOS and Linux: $(uname -s) is not supported." ;;
    esac
    case "$(uname -m)" in
        x86_64 | amd64) ARCH=x64 ;;
        arm64 | aarch64) ARCH=arm64 ;;
        *) die "CodeAlta runs on x64 and arm64 processors: $(uname -m) is not supported." ;;
    esac
    MUSL=0
    HEADLESS=0
    PM=""
    if [ "$OS" = macos ]; then
        # A terminal under Rosetta says x86_64 on Apple silicon.
        [ "$(sysctl -n hw.optional.arm64 2> /dev/null)" != 1 ] || ARCH=arm64
        SYSTEM="macOS $(sw_vers -productVersion 2> /dev/null)"
    else
        SYSTEM=Linux
        if [ -r /etc/os-release ]; then
            SYSTEM=$(. /etc/os-release 2> /dev/null && printf '%s' "${PRETTY_NAME:-${NAME:-Linux}}")
        fi
        # The C library of the system, not a musl installed beside glibc: getconf answers for glibc only.
        if ! getconf GNU_LIBC_VERSION > /dev/null 2>&1 && { ls /lib/ld-musl-* > /dev/null 2>&1 || (ldd --version 2>&1 | grep -qi musl); }; then MUSL=1; fi
        [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ] || HEADLESS=1
        for candidate in apt-get dnf zypper pacman apk; do
            if has "$candidate"; then
                PM=$candidate
                break
            fi
        done
    fi
    has curl || has wget || die "curl or wget is needed to download .NET." "Install one of them, then run this again."
    ok "$SYSTEM $S_DOT $ARCH"
}

# The command that installs packages with the package manager of the system, without questions.
pm_command() {
    case "$PM" in
        apt-get) printf 'apt-get install -y %s' "$*" ;;
        dnf) printf 'dnf install -y %s' "$*" ;;
        zypper) printf 'zypper --non-interactive install %s' "$*" ;;
        pacman) printf 'pacman -S --needed --noconfirm %s' "$*" ;;
        apk) printf 'apk add %s' "$*" ;;
    esac
}

# Prepares a command that needs administrator rights: sets $SUDO, and has sudo ask for the password now, in the open,
# so that the command itself runs behind the spinner. Fails when the rights cannot be had.
elevate() {
    SUDO=""
    [ "$(id -u)" != 0 ] || return 0
    has sudo || return 1
    SUDO=sudo
    if sudo -n true 2> /dev/null; then return 0; fi
    # Without a terminal to type the password in, sudo cannot ask.
    [ "$TTY" = 1 ] && [ -r /dev/tty ] || return 1
    info "$1 needs administrator rights: sudo asks for your password."
    sudo -v < /dev/tty
}

# Installs packages of the system: $1 says what for, the others name them. Fails when it could not.
pm_install() {
    local purpose command
    purpose=$1
    shift
    [ -n "$PM" ] || return 1
    command=$(pm_command "$@")
    if ! elevate "$purpose"; then
        LAST_COMMAND="sudo $command"
        return 1
    fi
    LAST_COMMAND="${SUDO:+sudo }$command"
    # apt-get asks nothing (the time zone, a changed configuration file) when it is told no one answers.
    [ "$PM" != apt-get ] || command="env DEBIAN_FRONTEND=noninteractive $command"
    # shellcheck disable=SC2086
    if run_quiet "Installing $purpose" $SUDO $command; then return 0; fi
    # A package list never downloaded (a new machine, a container) knows no package.
    if [ "$PM" = apt-get ]; then
        run_quiet "Updating the package lists" $SUDO apt-get update || return 1
        # shellcheck disable=SC2086
        run_quiet "Installing $purpose" $SUDO $command && return 0
    fi
    return 1
}

# --- .NET ---------------------------------------------------------------------------------------------------------------

# Whether the dotnet at $1 has what CodeAlta needs: an SDK 10 or later (dotnet tool is part of the SDK) and the released
# .NET 10 runtimes its apps run on, .NET and ASP.NET Core, .NET recent enough on macOS. Sets $SDK_VERSION to its newest SDK.
dotnet_ok() {
    local sdks runtimes minimum
    sdks=$("$1" --list-sdks 2> /dev/null) || return 1
    runtimes=$("$1" --list-runtimes 2> /dev/null) || return 1
    SDK_VERSION=$(printf '%s\n' "$sdks" | awk '{ split($1, v, "."); if (v[1] + 0 >= 10) print $1 }' | sort -t. -k1,1n -k2,2n -k3,3n | tail -n 1)
    [ -n "$SDK_VERSION" ] || return 1
    minimum=0
    [ "$OS" != macos ] || minimum=$MAC_MINIMUM_PATCH
    # A preview runtime (10.0.0-rc.1) does not run a released application.
    printf '%s\n' "$runtimes" | awk -v minimum="$minimum" '
        $2 !~ /-/ {
            split($2, v, ".")
            if (v[1] != 10) next
            if ($1 == "Microsoft.NETCore.App" && (v[2] > 0 || v[3] + 0 >= minimum)) core = 1
            if ($1 == "Microsoft.AspNetCore.App") web = 1
        }
        END { exit !(core && web) }'
}

# The folder of the .NET installation the dotnet at $1 runs, as it says itself: a dotnet may be a link, or the script of a
# version manager, far from it. Nothing when it has no installation behind it.
dotnet_root() {
    local root
    root=$("$1" --list-runtimes 2> /dev/null | sed -n 's|^Microsoft\.NETCore\.App [^ ]* \[\(.*\)/shared/Microsoft\.NETCore\.App\]$|\1|p' | head -n 1)
    if [ -z "$root" ]; then
        root=$(dirname "$(resolve "$1")")
        [ -d "$root/shared" ] || [ -d "$root/sdk" ] || root=""
    fi
    printf '%s\n' "$root"
}

# Whether programs find the .NET of that folder by themselves: where the system has .NET, or where it says it is.
dotnet_root_known() {
    case "$1" in /usr/share/dotnet | /usr/local/share/dotnet | /usr/lib/dotnet | /usr/lib64/dotnet) return 0 ;; esac
    cat /etc/dotnet/install_location /etc/dotnet/install_location_* 2> /dev/null | grep -qxF "$1"
}

# Looks for a dotnet that fits, on the PATH and where .NET is usually installed. Sets $DOTNET to it; otherwise
# $DOTNET_OTHER to a dotnet that does not fit, when there is one.
find_dotnet() {
    local candidate
    DOTNET=""
    DOTNET_OTHER=""
    for candidate in "$(command -v dotnet 2> /dev/null)" "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" "$HOME/.dotnet/dotnet" \
        /usr/local/share/dotnet/dotnet /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /usr/lib64/dotnet/dotnet \
        /opt/homebrew/bin/dotnet /usr/local/bin/dotnet /snap/bin/dotnet; do
        [ -n "$candidate" ] && [ -x "$candidate" ] || continue
        if dotnet_ok "$candidate"; then
            DOTNET=$candidate
            return 0
        fi
        [ -n "$DOTNET_OTHER" ] || DOTNET_OTHER=$candidate
    done
    return 1
}

# The ICU package of Debian and Ubuntu, named after its version (libicu74).
apt_icu_package() {
    apt-cache pkgnames libicu 2> /dev/null | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1
}

# What .NET itself needs on Linux and the system may lack: dotnet-install does not install it.
ensure_dotnet_libraries() {
    local missing packages
    [ "$OS" = linux ] || return 0
    missing=""
    has_lib libicuuc || missing=icu
    has_lib libstdc++.so || missing="$missing stdc++"
    [ -n "$missing" ] || return 0
    case "$PM" in
        apt-get)
            packages=""
            case "$missing" in
                *icu*)
                    packages=$(apt_icu_package)
                    # A package list never downloaded (a new machine, a container) names no package.
                    if [ -z "$packages" ] && elevate "The package lists" && run_quiet "Updating the package lists" $SUDO apt-get update; then
                        packages=$(apt_icu_package)
                    fi
                    ;;
            esac
            case "$missing" in *stdc++*) packages="$packages libstdc++6" ;; esac
            ;;
        dnf | zypper) packages="libicu libstdc++" ;;
        pacman) packages="icu gcc-libs" ;;
        apk) packages="icu-libs libstdc++ libgcc" ;;
        *) packages="" ;;
    esac
    # shellcheck disable=SC2086
    if [ -n "$(printf '%s' "$packages" | tr -d ' ')" ] && pm_install "the libraries of .NET" $packages; then return 0; fi
    show_log
    die ".NET needs libraries this system lacks (ICU, the C++ runtime)." \
        "Install them${LAST_COMMAND:+ with:  $LAST_COMMAND}" "then run this again. See https://learn.microsoft.com/dotnet/core/install/linux"
}

# Installs the .NET SDK in the folder of the user, ~/.dotnet, with the dotnet-install script of Microsoft.
install_dotnet_user() {
    local directory label
    directory=$USER_DOTNET
    label="Installing the runtime (.NET $DOTNET_CHANNEL, a few minutes on a slow connection)"
    has bash || die "bash is needed by the install script of .NET." "Install bash, then run this again."
    has tar || die "tar is needed by the install script of .NET." "Install tar, then run this again."
    ensure_dotnet_libraries
    download "$DOTNET_INSTALL_URL" "$WORK/dotnet-install.sh" || die "The install script of .NET could not be downloaded from $DOTNET_INSTALL_URL." "Check your connection, then run this again."
    if ! run_quiet "$label" bash "$WORK/dotnet-install.sh" --channel "$DOTNET_CHANNEL" --install-dir "$directory" --architecture "$ARCH" --no-path; then
        show_log
        die ".NET $DOTNET_CHANNEL could not be installed in $directory." "Install the .NET $DOTNET_CHANNEL SDK from $DOTNET_DOWNLOAD_PAGE, then run this again."
    fi
}

# Updates a .NET that belongs to the system, where the user cannot write: with what installed it.
install_dotnet_system() {
    local found package packages
    found=$1
    info "A .NET without the .NET $DOTNET_CHANNEL SDK is installed in $found: updating it."
    if [ "$OS" = macos ]; then
        case "$found" in
            */Cellar/* | */homebrew/* | */Homebrew/*)
                if has brew && run_quiet "Updating .NET with Homebrew" brew upgrade dotnet; then return 0; fi
                ;;
        esac
        package="$WORK/dotnet-sdk.pkg"
        run_quiet "Downloading the .NET $DOTNET_CHANNEL SDK" download "https://aka.ms/dotnet/$DOTNET_CHANNEL/dotnet-sdk-osx-$ARCH.pkg" "$package" \
            || die "The .NET $DOTNET_CHANNEL SDK could not be downloaded." "Install it from $DOTNET_DOWNLOAD_PAGE, then run this again."
        elevate "The installer of .NET" || die "Administrator rights are needed to update .NET." "Install the .NET $DOTNET_CHANNEL SDK from $DOTNET_DOWNLOAD_PAGE, then run this again."
        run_quiet "Installing .NET $DOTNET_CHANNEL" $SUDO installer -pkg "$package" -target / || {
            show_log
            die ".NET $DOTNET_CHANNEL could not be installed." "Install the .NET $DOTNET_CHANNEL SDK from $DOTNET_DOWNLOAD_PAGE, then run this again."
        }
        return 0
    fi
    case "$PM" in
        apt-get | dnf | zypper) packages="dotnet-sdk-$DOTNET_CHANNEL" ;;
        pacman) packages="dotnet-sdk" ;;
        apk) packages="dotnet10-sdk" ;;
        *) packages="" ;;
    esac
    LAST_COMMAND=""
    # shellcheck disable=SC2086
    if [ -n "$packages" ] && pm_install ".NET $DOTNET_CHANNEL" $packages; then return 0; fi
    show_log
    die "The .NET $DOTNET_CHANNEL SDK could not be installed with the package manager of the system." \
        "Install it${LAST_COMMAND:+ with:  $LAST_COMMAND}" "or as https://learn.microsoft.com/dotnet/core/install/linux says, then run this again."
}

ensure_dotnet() {
    local directory
    USER_DOTNET="$HOME/.dotnet"
    # A .NET that fits is used as it is, wherever it is: nothing is installed.
    if ! find_dotnet; then
        directory=""
        [ -z "$DOTNET_OTHER" ] || directory=$(dotnet_root "$DOTNET_OTHER")
        # A .NET of the system that is too old is updated by the system, so that the system keeps one .NET. Anything else
        # (no .NET, one a version manager keeps for the user) is left as it is, and .NET goes to ~/.dotnet.
        if [ -n "$directory" ] && [ ! -w "$directory" ] && [ "$directory" != "$(physical "$USER_DOTNET")" ]; then
            install_dotnet_system "$directory"
        else
            install_dotnet_user
        fi
    fi
    find_dotnet || die "The .NET $DOTNET_CHANNEL SDK was installed, but no dotnet that has it is found." \
        "Install it from $DOTNET_DOWNLOAD_PAGE, then run this again."
    DOTNET_DIR=$(dotnet_root "$DOTNET")
    # Named as the home folder names it, where that folder is reached through a link (/tmp on macOS, a home on another disk).
    [ "$DOTNET_DIR" != "$(physical "$USER_DOTNET")" ] || DOTNET_DIR=$USER_DOTNET
    ok "Runtime $C_DIM$S_DOT .NET SDK $SDK_VERSION $S_DOT $DOTNET_DIR$C_RESET"
    SHELL_DOTNET_ROOT=${DOTNET_ROOT:-}
    DOTNET_ROOT=$DOTNET_DIR
    DOTNET_CLI_TELEMETRY_OPTOUT=1
    DOTNET_NOLOGO=1
    export DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_NOLOGO
}

# --- CodeAlta -----------------------------------------------------------------------------------------------------------

choose_apps() {
    [ -z "$APPS" ] || return 0
    APPS=desktop
    [ "$OS" = linux ] || return 0
    if [ "$MUSL" = 1 ]; then
        APPS=tui
        info "The desktop app does not run on musl (Alpine): installing CodeAlta TUI, the terminal app."
    elif [ "$HEADLESS" = 1 ]; then
        APPS=tui
        info "No desktop session here: installing CodeAlta TUI, the terminal app (--desktop installs the desktop app)."
    fi
}

# The desktop app draws its window with the web view of the system: GTK 4 and WebKitGTK 6.0 on Linux.
ensure_desktop_libraries() {
    local missing library packages
    [ "$OS" = linux ] || return 0
    case " $APPS " in *" desktop "*) ;; *) return 0 ;; esac
    if [ "$MUSL" = 1 ]; then
        warn "CodeAlta Desktop has no build for musl (Alpine): use CodeAlta TUI (--tui)."
        DESKTOP_READY=0
        return 0
    fi
    # The libraries the window loads: without one of them CodeAlta stops at its start with "Missing: libwebkitgtk-6.0.so.4".
    missing=""
    for library in libgtk-4.so.1 libwebkitgtk-6.0.so.4 libjavascriptcoregtk-6.0.so.1 libsoup-3.0.so.0; do
        has_lib "$library" || missing="$missing $library"
    done
    if [ -z "$missing" ]; then
        ok "GTK 4 and WebKitGTK 6.0"
        return 0
    fi
    case "$PM" in
        apt-get) packages="libgtk-4-1 libwebkitgtk-6.0-4" ;;
        dnf) packages="gtk4 webkitgtk6.0" ;;
        zypper) packages="libgtk-4-1 libwebkitgtk-6_0-4" ;;
        pacman) packages="gtk4 webkitgtk-6.0" ;;
        *) packages="" ;;
    esac
    LAST_COMMAND=""
    # shellcheck disable=SC2086
    if [ -n "$packages" ] && pm_install "GTK 4 and WebKitGTK 6.0, for the window of the desktop app" $packages; then
        ok "GTK 4 and WebKitGTK 6.0"
        return 0
    fi
    # Not a reason to stop: the terminal app and the command line do without them.
    DESKTOP_LIBRARIES=missing
    DESKTOP_READY=0
    warn "The desktop app needs GTK 4 and WebKitGTK 6.0, which could not be installed:$missing"
    [ -z "$LAST_COMMAND" ] || detail "Install them with:  $LAST_COMMAND"
    detail "See $GUIDE#desktop-requirements"
}

# Asks the system what the installed desktop app still lacks to open its window: every library its native part
# links to, whatever the distribution names their packages.
verify_desktop() {
    local native missing
    [ "$OS" = linux ] && [ "$MUSL" = 0 ] && [ -z "$DESKTOP_LIBRARIES" ] && has ldd || return 0
    case " $APPS " in *" desktop "*) ;; *) return 0 ;; esac
    native=$(find "$HOME/.dotnet/tools/.store" -path "*linux-$ARCH*" -name 'libneoastra_native.so' 2> /dev/null | head -n 1)
    [ -n "$native" ] || return 0
    missing=$(ldd "$native" 2> /dev/null | awk '/not found/ { printf " %s", $1 }')
    [ -n "$missing" ] || return 0
    DESKTOP_READY=0
    warn "The desktop app cannot open its window yet: the system lacks$missing"
    detail "Install the packages that provide them (GTK 4, WebKitGTK 6.0, libsoup 3). See $GUIDE#desktop-requirements"
}

# Ubuntu 24.04 and later let only the programs with an AppArmor profile that says so create user namespaces, which the
# web view (WebKitGTK) needs for the sandbox of its pages: without one, the window aborts as it opens. Ubuntu ships
# such a profile for each browser it knows; this gives CodeAlta the same one.
ensure_desktop_sandbox() {
    local parser profile home candidate
    [ "$OS" = linux ] && [ "$MUSL" = 0 ] || return 0
    case " $APPS " in *" desktop "*) ;; *) return 0 ;; esac
    [ "$(cat /proc/sys/kernel/apparmor_restrict_unprivileged_userns 2> /dev/null)" = 1 ] || return 0
    parser=""
    for candidate in /usr/sbin/apparmor_parser /sbin/apparmor_parser; do
        [ ! -x "$candidate" ] || parser=$candidate
    done
    # The folders of the users, as AppArmor names them; this home folder itself when it is elsewhere.
    case "$HOME" in /home/*/* | /root/*) home=$HOME ;; /home/* | /root) home='@{HOME}' ;; *) home=$HOME ;; esac
    # A space or a character AppArmor reads as a pattern in that name would make another profile than the one meant.
    case "$home" in
        '@{HOME}') ;;
        *[!A-Za-z0-9/._-]*)
            DESKTOP_READY=0
            warn "This system keeps the window of the desktop app from opening until CodeAlta has an AppArmor profile, which this script does not write for the home folder $HOME."
            detail "See $TROUBLESHOOTING#the-desktop-app-does-not-start"
            return 0
            ;;
    esac
    profile="$WORK/codealta.apparmor"
    cat > "$profile" << EOF
# CodeAlta Desktop (https://codealta.io): lets its web view, WebKitGTK, start the sandbox of its pages.
# Written by the CodeAlta installer, as the profiles Ubuntu ships for the browsers.
abi <abi/4.0>,
include <tunables/global>

profile codealta $home/.dotnet/tools/{alta,.store/codealta*/**/alta} flags=(unconfined) {
  userns,

  # Site-specific additions and overrides. See local/README for details.
  include if exists <local/codealta>
}
EOF
    # The file is written once the profile is loaded: the same file there is a profile the system has, and loads at each start.
    if [ -f /etc/apparmor.d/codealta ] && cmp -s "$profile" /etc/apparmor.d/codealta; then
        ok "AppArmor profile $C_DIM$S_DOT /etc/apparmor.d/codealta$C_RESET"
        return 0
    fi
    # shellcheck disable=SC2016
    if [ -n "$parser" ] && [ -d /etc/apparmor.d ] && elevate "The AppArmor profile that lets the window of the desktop app open"         && run_quiet "Installing the AppArmor profile of CodeAlta" $SUDO sh -c '"$2" -r "$1" && cp "$1" /etc/apparmor.d/codealta && chmod 644 /etc/apparmor.d/codealta' sh "$profile" "$parser"; then
        ok "AppArmor profile $C_DIM$S_DOT /etc/apparmor.d/codealta$C_RESET"
        return 0
    fi
    DESKTOP_READY=0
    warn "This system keeps the window of the desktop app from opening until CodeAlta has an AppArmor profile, which could not be installed."
    [ "$TTY" = 1 ] || detail "Run this again in a terminal, where sudo can ask for your password."
    detail "See $TROUBLESHOOTING#the-desktop-app-does-not-start"
}

installed_version() {
    "$DOTNET" tool list -g 2> /dev/null | awk -v id="$1" 'tolower($1) == tolower(id) { print $2 }'
}

install_app() {
    local package title before after
    package=$1 title=$2
    before=$(installed_version "$package")
    set -- tool update -g "$package" --source "$NUGET_SOURCE"
    if [ -n "$VERSION" ]; then set -- "$@" --version "$VERSION" --allow-downgrade; elif [ "$PRERELEASE" = 1 ]; then set -- "$@" --prerelease; fi
    if ! run_quiet "Installing $title" "$DOTNET" "$@"; then
        show_log
        die "$title could not be installed." "The command was:  dotnet $*" "Help: $GUIDE"
    fi
    after=$(installed_version "$package")
    if [ -z "$before" ]; then
        ok "$title $after"
    elif [ "$before" = "$after" ]; then
        ok "$title $after $C_DIM$S_DOT up to date$C_RESET"
    else
        ok "$title $after $C_DIM$S_DOT updated from $before$C_RESET"
    fi
}

install_apps() {
    local app
    COMMANDS=""
    for app in $APPS; do
        case "$app" in
            desktop) install_app CodeAlta "CodeAlta Desktop"; COMMANDS="$COMMANDS alta" ;;
            tui) install_app CodeAlta.Tui "CodeAlta TUI"; COMMANDS="$COMMANDS altatui" ;;
            cli) install_app CodeAlta.Cli "CodeAlta CLI"; COMMANDS="$COMMANDS altacli" ;;
        esac
    done
}

# --- PATH ---------------------------------------------------------------------------------------------------------------

on_path() {
    case ":$PATH:" in *":$1:"*) return 0 ;; *) return 1 ;; esac
}

# Writes $HOME as the variable in what goes to a profile, so that the file stays right when the home folder moves.
home_relative() {
    case "$1" in
        "$HOME"/*) printf '$HOME%s' "${1#"$HOME"}" ;;
        *) printf '%s' "$1" ;;
    esac
}

# Adds a line to a profile of the shell, once.
add_line() {
    local file line
    file=$1 line=$2
    if [ ! -f "$file" ] || ! grep -qF "$line" "$file"; then
        mkdir -p "$(dirname "$file")" || return 1
        if [ ! -f "$file" ] || ! grep -qF "$PROFILE_MARK" "$file"; then printf '\n%s\n' "$PROFILE_MARK" >> "$file" || return 1; fi
        printf '%s\n' "$line" >> "$file" || return 1
    fi
    PROFILE=$file
}

# Makes the commands found in new terminals: the folder of the .NET tools on the PATH, and .NET itself when this
# script installed it in the home folder.
setup_path() {
    local additions root shell file
    TOOLS="$HOME/.dotnet/tools"
    PROFILE_MARK="# Added by the CodeAlta installer (https://codealta.io)"
    PROFILE=""
    RELOAD=""
    additions=""
    root=""
    # The commands are small programs that look for .NET where the system has it: DOTNET_ROOT names one that is elsewhere.
    if ! dotnet_root_known "$DOTNET_DIR" && [ "$SHELL_DOTNET_ROOT" != "$DOTNET_DIR" ]; then root=$(home_relative "$DOTNET_DIR"); fi
    if [ "$DOTNET_DIR" = "$USER_DOTNET" ] && ! on_path "$DOTNET_DIR"; then additions=$(home_relative "$DOTNET_DIR"); fi
    on_path "$TOOLS" || additions="${additions:+$additions:}$(home_relative "$TOOLS")"
    [ -n "$root$additions" ] || return 0
    RELOAD=1
    if [ "$MODIFY_PATH" = 0 ]; then
        # The commands are then started by their path, with the variable they need.
        RELOAD=path
        NEEDS_ROOT=$root
        info "The profile of your shell is left as it is."
        return 0
    fi
    shell=$(basename "${SHELL:-sh}")
    case "$shell" in
        fish)
            file="${XDG_CONFIG_HOME:-$HOME/.config}/fish/conf.d/codealta.fish"
            [ -z "$root" ] || add_line "$file" "set -gx DOTNET_ROOT \"$root\""
            [ -z "$additions" ] || add_line "$file" "fish_add_path --append $(printf '%s' "$additions" | tr ':' ' ')"
            ;;
        *)
            case "$shell" in
                zsh) file="${ZDOTDIR:-$HOME}/.zshrc" ;;
                bash)
                    file="$HOME/.bashrc"
                    # The terminals of macOS start login shells, which read the first of three files: the one that is
                    # there, since a new .bash_profile would hide the two others.
                    if [ "$OS" = macos ]; then
                        file="$HOME/.bash_profile"
                        if [ ! -f "$file" ] && [ -f "$HOME/.bash_login" ]; then file="$HOME/.bash_login"; fi
                        if [ ! -f "$file" ] && [ -f "$HOME/.profile" ]; then file="$HOME/.profile"; fi
                    fi
                    ;;
                *) file="$HOME/.profile" ;;
            esac
            [ -z "$root" ] || add_line "$file" "export DOTNET_ROOT=\"$root\""
            [ -z "$additions" ] || add_line "$file" "export PATH=\"\$PATH:$additions\""
            ;;
    esac
    if [ -n "$PROFILE" ]; then
        ok "PATH $C_DIM$S_DOT $(home_relative "$PROFILE" | sed 's/^\$HOME/~/')$C_RESET"
    else
        warn "Add $TOOLS to the PATH of your shell to find the commands."
    fi
    RELOAD=1
}

finish() {
    local first other launch
    # shellcheck disable=SC2086
    set -- $COMMANDS
    first=$1
    [ -x "$TOOLS/$first" ] || die "$first was installed, but is not found in $TOOLS." "Help: $GUIDE"
    launch=$first
    [ "$RELOAD" != path ] || launch="$TOOLS/$first"
    if [ "$first" = alta ] && [ "$DESKTOP_READY" = 0 ]; then
        printf '\n  %sCodeAlta Desktop is installed, but its window cannot open yet.%s\n' "$C_BOLD" "$C_RESET"
        printf '  Do what the lines marked %s!%s above say, then ' "$C_WARN" "$C_RESET"
        if [ "$RELOAD" = 1 ]; then printf 'open a new terminal and '; fi
        printf 'start it from a project folder:\n'
    elif [ "$RELOAD" = 1 ]; then
        printf '\n  %sCodeAlta is ready.%s Open a new terminal, then start it from a project folder:\n' "$C_BOLD" "$C_RESET"
    else
        printf '\n  %sCodeAlta is ready.%s Start it from a project folder:\n' "$C_BOLD" "$C_RESET"
    fi
    printf '\n      %s%s%s\n\n' "$C_BOLD$C_ACCENT" "$launch" "$C_RESET"
    [ -z "$NEEDS_ROOT" ] || detail "It needs this variable: export DOTNET_ROOT=\"$NEEDS_ROOT\""
    shift
    for other in "$@"; do detail "$other is installed too."; done
    detail "First steps: $GUIDE"
    printf '\n'
}

usage() {
    # Read from a pipe, the script has no file to show its header from.
    if [ -f "$0" ] && grep -q '^# CodeAlta installer' "$0"; then
        sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'
    else
        printf 'CodeAlta installer: curl -fsSL https://codealta.io/install.sh | sh -s -- [--desktop] [--tui] [--cli] [--all] [--prerelease] [--version <v>] [--no-modify-path]\n'
    fi
}

main() {
    APPS=""
    PRERELEASE=0
    MODIFY_PATH=1
    VERSION=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --desktop) APPS="$APPS desktop" ;;
            --tui) APPS="$APPS tui" ;;
            --cli) APPS="$APPS cli" ;;
            --all) APPS="desktop tui cli" ;;
            --prerelease) PRERELEASE=1 ;;
            --no-modify-path) MODIFY_PATH=0 ;;
            --version)
                [ $# -ge 2 ] || { printf 'install.sh: --version needs a version\n' >&2; exit 2; }
                VERSION=$2
                shift
                ;;
            -h | --help)
                usage
                exit 0
                ;;
            *)
                printf 'install.sh: unknown option %s (--help lists the options)\n' "$1" >&2
                exit 2
                ;;
        esac
        shift
    done
    APPS=$(printf '%s\n' $APPS | awk '!seen[$0]++' | tr '\n' ' ' | sed 's/ *$//')
    [ -n "${HOME:-}" ] || { printf 'install.sh: HOME is not set\n' >&2; exit 1; }

    setup_style
    CHILD=""
    SUDO=""
    LAST_COMMAND=""
    DESKTOP_LIBRARIES=""
    DESKTOP_READY=1
    NEEDS_ROOT=""
    WORK=$(mktemp -d 2> /dev/null || mktemp -d -t codealta) || { printf 'install.sh: no temporary folder\n' >&2; exit 1; }
    LOG="$WORK/output.log"
    trap cleanup EXIT
    trap 'exit 130' INT TERM
    # dotnet reads the global.json and the nuget.config of the folder it runs in: not the ones of a project the
    # terminal happens to be in.
    cd "$WORK" || exit 1

    banner
    detect_platform
    choose_apps
    ensure_dotnet
    ensure_desktop_libraries
    install_apps
    verify_desktop
    ensure_desktop_sandbox
    setup_path
    finish
}

main "$@"
