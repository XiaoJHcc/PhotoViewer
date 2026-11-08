#!/bin/bash
# 产品身份映射读取器：edition 是唯一外部输入，取值只来自根 Directory.Build.props。
# 由 PhotoViewer.iOS 的发布 / 归档 / 巡检脚本 source，调用方负责格式化报错；
# 本文件不另立产品判断，映射缺失或含未展开表达式时以明确错误返回非 0。

pv_props_path() {
    printf '%s\n' "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/Directory.Build.props"
}

# 取某个身份属性在指定 edition 下的值。同一属性按 edition 分行声明，无条件的行两版共用。
pv_prop_for_edition() {
    local props="$1"
    local name="$2"
    local edition="$3"
    local sep attrs value
    # 分隔符必须是非空白字符：IFS 空白会被 read 吞掉，让空属性段与值混成一段。
    sep=$'\x1f'
    while IFS="${sep}" read -r attrs value; do
        if [[ -z "${attrs}${value}" ]]; then
            continue
        fi
        if [[ "${attrs}" == *"Condition="* && "${attrs}" != *"== '${edition}'"* ]]; then
            continue
        fi
        printf '%s' "${value//\$(PhotoViewerEdition)/${edition}}"
        return 0
    done < <(sed -n "s|^[[:space:]]*<${name}\([^>]*\)>\(.*\)</${name}>[[:space:]]*\$|\1${sep}\2|p" "${props}")
    return 1
}

# 载入 edition 映射，结果写入 PV_PRODUCT_ID / PV_PACKAGE_ID / PV_DISPLAY_NAME /
# PV_DISTRIBUTION_NAME / PV_ENABLE_DINO。
pv_load_edition_identity() {
    local edition="$1"
    local props="${2:-}"
    local field name value
    case "${edition}" in
        standard|ai) ;;
        *)
            echo "FAIL: edition 只接受 standard 或 ai：${edition:-<空>}" >&2
            return 1
            ;;
    esac
    if [[ -z "${props}" ]]; then
        props="$(pv_props_path)"
    fi
    if [[ ! -f "${props}" ]]; then
        echo "FAIL: 未找到产品身份来源 ${props}" >&2
        return 1
    fi
    for field in ProductId PackageId DisplayName DistributionName EnableDino; do
        name="PhotoViewer${field}"
        if ! value="$(pv_prop_for_edition "${props}" "${name}" "${edition}")" || [[ -z "${value}" ]]; then
            echo "FAIL: ${props} 缺少 ${edition} 版的 <${name}>；产品身份应只在根 props 定义。" >&2
            return 1
        fi
        case "${value}" in
            *'$('*)
                echo "FAIL: ${props} 的 ${name}（${edition}）含未展开的 MSBuild 表达式：${value}" >&2
                return 1
                ;;
        esac
        case "${field}" in
            ProductId) PV_PRODUCT_ID="${value}" ;;
            PackageId) PV_PACKAGE_ID="${value}" ;;
            DisplayName) PV_DISPLAY_NAME="${value}" ;;
            DistributionName) PV_DISTRIBUTION_NAME="${value}" ;;
            EnableDino) PV_ENABLE_DINO="${value}" ;;
        esac
    done
    case "${PV_ENABLE_DINO}" in
        true|false) ;;
        *)
            echo "FAIL: ${props} 的 ${edition} 版 PhotoViewerEnableDino 应为 true|false，实际 ${PV_ENABLE_DINO}" >&2
            return 1
            ;;
    esac
    return 0
}

# 由实际 Bundle ID 反查 edition；两个产品都不是时返回非 0。
pv_edition_for_package_id() {
    local package_id="$1"
    local props="${2:-}"
    local edition
    if [[ -z "${props}" ]]; then
        props="$(pv_props_path)"
    fi
    for edition in standard ai; do
        if [[ "$(pv_prop_for_edition "${props}" PhotoViewerPackageId "${edition}")" == "${package_id}" ]]; then
            printf '%s\n' "${edition}"
            return 0
        fi
    done
    return 1
}

# 读取描述文件映射：ExportOptions 的 provisioningProfiles 以 Bundle ID 为键。
pv_require_profile_mapping() {
    local plist="$1"
    local package_id="$2"
    local edition="$3"
    local value
    if [[ ! -f "${plist}" ]]; then
        echo "FAIL: 缺少签名资料映射 ${plist}" >&2
        return 1
    fi
    value="$(/usr/libexec/PlistBuddy -c "Print :provisioningProfiles:${package_id}" "${plist}" 2>/dev/null || true)"
    if [[ -z "${value}" ]]; then
        echo "FAIL: ${plist} 的 provisioningProfiles 缺少 ${edition} 版 Bundle ID ${package_id}。" >&2
        echo "      本地 plist 必须同时填写两个 Bundle ID 到描述文件名的映射，见同目录 .sample.plist。" >&2
        return 1
    fi
    printf '%s' "${value}"
}
