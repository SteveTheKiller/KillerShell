# KillerShell Bash prompt. Runs only in Bash sessions started by KillerShell.
# This copy belongs to you. Upgrades refresh KillerPrompt.default.bash beside it.
# ~/.bashrc is loaded first and is never modified.
# Run restore_prompt to use your original prompt for this session.
# Set KS_PROMPT=0 before launching KillerShell to skip this script.

[[ ${__ks_prompt_loaded:-0} == 1 ]] && return
__ks_prompt_loaded=1
__ks_native_prompt=$PS1
__ks_native_promptvars=0
shopt -q promptvars && __ks_native_promptvars=1
shopt -s promptvars
__ks_enabled=1
__ks_first=1
declare -A __ks_palette=([ACCENT]='#e8485a' [FG]='#fffde8' [DIM]='#e2b58a' [OK]='#5cb85c' [WARN]='#e8b45c')

restore_prompt() {
    __ks_enabled=0
    PS1=$__ks_native_prompt
    (( __ks_native_promptvars )) || shopt -u promptvars
}

__ks_capture_status() {
    __ks_status=$?
    return "$__ks_status"
}

# Read data, never shell code. Keep the last palette if the file is unavailable.
__ks_read_palette() {
    [[ -n ${KS_STATE:-} && -r $KS_STATE ]] || return 0
    local key value
    while IFS='=' read -r key value; do
        value=${value%$'\r'}
        case $key in ACCENT|FG|DIM|OK|WARN)
            [[ $value =~ ^#[[:xdigit:]]{6}$ ]] && __ks_palette[$key]=$value ;;
        esac
    done < "$KS_STATE"
    return 0
}

# Readline must exclude color escapes from its cursor and wrapping calculations.
__ks_color() {
    local hex=${__ks_palette[$2]} mode=${3:-38}
    printf -v "$1" '\\[\\e[%s;2;%d;%d;%dm\\]' "$mode" \
        "$((16#${hex:1:2}))" "$((16#${hex:3:2}))" "$((16#${hex:5:2}))"
}

__ks_format_path() {
    __ks_path=$PWD
    if [[ $PWD == "$HOME" ]]; then __ks_path='~'
    elif [[ $PWD == "$HOME/"* ]]; then __ks_path="~${PWD#"$HOME"}"
    fi
    local -a parts
    local i
    IFS='/' read -r -a parts <<< "$__ks_path"
    if (( ${#parts[@]} > 4 )); then
        for (( i=0; i<${#parts[@]}-3; i++ )); do
            parts[i]=${parts[i]:0:1}
        done
        local IFS='/'
        __ks_path="${parts[*]}"
    fi
}

__ks_format_git() {
    __ks_git=''
    __ks_git_color=$__ks_ok
    local probe=$PWD head line status
    while [[ ! -e $probe/.git ]]; do
        [[ $probe == / ]] && return 0
        probe=${probe%/*}
        [[ -n $probe ]] || probe=/
    done
    status=$(git status --porcelain=v1 --branch --untracked-files=normal 2>/dev/null) || return 0
    head=${status%%$'\n'*}
    __ks_git=${head#'## '}
    __ks_git=${__ks_git%%...*}
    __ks_git=${__ks_git%%' ['*}
    [[ $__ks_git == HEAD* ]] && __ks_git=detached
    __ks_git=$' \uE0A0 '"$__ks_git"
    if [[ $status == *$'\n'* ]]; then
        __ks_git+=$' \u00B1'
        __ks_git_color=$__ks_warn
    fi
    [[ $head =~ ahead\ ([0-9]+) ]] && __ks_git+=$' \u2191'"${BASH_REMATCH[1]}"
    [[ $head =~ behind\ ([0-9]+) ]] && __ks_git+=$' \u2193'"${BASH_REMATCH[1]}"
    return 0
}

__ks_render_prompt() {
    (( __ks_enabled )) || return "$__ks_status"
    __ks_read_palette
    __ks_color __ks_accent ACCENT
    __ks_color __ks_on_accent ACCENT 48
    __ks_color __ks_fg FG
    __ks_color __ks_ok OK
    __ks_color __ks_warn WARN
    __ks_format_path
    __ks_format_git
    __ks_label="${KS_DISTRO:-${WSL_DISTRO_NAME:-Linux}} | ${USER:-user}@${HOSTNAME%%.*}:$__ks_path"
    __ks_root=''
    (( EUID == 0 )) && __ks_root=' [ROOT]'
    local reset='\[\e[0m\]' mark=$__ks_accent
    (( __ks_status != 0 )) && mark=$__ks_warn
    PS1=''
    (( __ks_first )) || PS1='\n'
    __ks_first=0
    # Expand path and branch variables only when Bash prints the prompt. Their
    # contents remain literal, even when names contain dollars or backticks.
    PS1+="$__ks_on_accent$__ks_fg"' ${__ks_label} '"$reset$__ks_accent"$'\uE0B0'"$reset"
    PS1+="$__ks_git_color"'${__ks_git}'"$reset$__ks_accent"'${__ks_root}'"$reset"
    PS1+='\n'"$mark"$'\u276F '"$reset"
    return "$__ks_status"
}

# Capture the command's status before existing hooks run. Keep scalar and array
# hooks in their original order and add formatting after them.
PROMPT_COMMAND=(__ks_capture_status "${PROMPT_COMMAND[@]}" __ks_render_prompt)
