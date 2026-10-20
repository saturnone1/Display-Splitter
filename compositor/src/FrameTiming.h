#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>

namespace ds {
// Render-thread-only rolling samples. No allocation on the frame path.
template<std::size_t Capacity = 256>
class FrameTiming {
    static_assert(Capacity > 0);
    std::array<double, Capacity> m_values{};
    std::size_t m_count = 0, m_next = 0;
public:
    void Add(double milliseconds) {
        if (!std::isfinite(milliseconds) || milliseconds < 0) return;
        m_values[m_next] = milliseconds;
        m_next = (m_next + 1) % Capacity;
        m_count = std::min(Capacity, m_count + 1);
    }
    double Percentile(double fraction) const {
        if (m_count == 0 || !std::isfinite(fraction)) return 0;
        auto sorted = m_values;
        std::sort(sorted.begin(), sorted.begin() + m_count);
        const auto rank = static_cast<std::size_t>(std::ceil(std::clamp(fraction, 0.0, 1.0) * static_cast<double>(m_count)));
        return sorted[rank > 0 ? rank - 1 : 0];
    }
    std::size_t Count() const { return m_count; }
};
}
