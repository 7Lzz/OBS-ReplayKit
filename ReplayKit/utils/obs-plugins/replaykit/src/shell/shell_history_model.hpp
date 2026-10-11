// the list of views the shell has been to and where it stands in it, the way the history of a browser keeps its pages. no qt and no obs in here, so it can be tested on its own
#pragma once

#include <cstddef>
#include <functional>
#include <vector>

#include "shell/shell_types.hpp"

namespace rk {

class ShellHistoryModel {
public:
	explicit ShellHistoryModel(std::size_t limit = 40) : m_limit(limit) {}

	// the history starts at the view the shell opens on
	void Reset(ShellView first)
	{
		m_entries.assign(1, first);
		m_index = 0;
	}

	// a view the person came to: everything ahead of the current place is dropped, and the view they are already on is not a new place
	void Note(ShellView view)
	{
		if (m_index >= 0 && m_entries[m_index] == view)
			return;
		m_entries.resize(static_cast<std::size_t>(m_index + 1));
		m_entries.push_back(view);
		if (m_entries.size() > m_limit)
			m_entries.erase(m_entries.begin());
		m_index = static_cast<int>(m_entries.size()) - 1;
	}

	// the entry a step in that direction lands on, or -1; entries the predicate turns down are passed over
	int Target(int direction, const std::function<bool(ShellView)> &openable) const
	{
		for (int at = m_index + direction; at >= 0 && at < static_cast<int>(m_entries.size()); at += direction) {
			if (openable(m_entries[static_cast<std::size_t>(at)]))
				return at;
		}
		return -1;
	}

	ShellView At(int index) const { return m_entries[static_cast<std::size_t>(index)]; }
	void Land(int index) { m_index = index; }
	int Index() const { return m_index; }
	std::size_t Size() const { return m_entries.size(); }

private:
	std::vector<ShellView> m_entries;
	int m_index = -1;
	std::size_t m_limit;
};

} // namespace rk
