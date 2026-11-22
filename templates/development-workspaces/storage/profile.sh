# Workspaces storage tiers: login shells of the coding user (UID 2000) use the same scratch-disk
# cache and temporary directories as workspaces-t3.service. Other users, including root, are unchanged.
if [ "$(id -u 2>/dev/null)" = 2000 ] && [ -r /opt/workspaces-workspace/scratch.env ]; then
    while IFS='=' read -r workspaces_key workspaces_value; do
        case "$workspaces_key" in
            ''|'#'*) ;;
            *) export "$workspaces_key=$workspaces_value" ;;
        esac
    done < /opt/workspaces-workspace/scratch.env
    unset workspaces_key workspaces_value
fi
