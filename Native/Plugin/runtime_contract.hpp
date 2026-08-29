#pragma once

#include <cstdint>
#include <span>
#include <string_view>

namespace dmc5ds::runtime_contract {

constexpr int kAttackLargeAction = 1;
constexpr int kSpecial2Action = 14;

struct BindingLink {
    int action{-1};
    std::uint32_t button{};
};

struct ControlBindings {
    bool has_attack_large{};
    bool has_special2{};
    std::uint32_t attack_large{};
    std::uint32_t special2{};

    bool complete_for(std::string_view character) const {
        return has_attack_large && (character != "nero" || has_special2);
    }
};

inline ControlBindings resolve_bindings(std::span<const BindingLink> links) {
    ControlBindings result;
    for (const auto& link : links) {
        if (link.action == kAttackLargeAction) {
            result.has_attack_large = true;
            result.attack_large = link.button;
        } else if (link.action == kSpecial2Action) {
            result.has_special2 = true;
            result.special2 = link.button;
        }
    }
    return result;
}

} // namespace dmc5ds::runtime_contract
