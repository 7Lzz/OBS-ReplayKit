-- called on startup. the ReplayKit plugin knows whether the clip buffer lives in memory or on disk and starts that one; the obs replay buffer is started here only when the plugin is not loaded
function script_load(settings)
	local cd = obslua.calldata_create()
	local called = obslua.proc_handler_call(obslua.obs_get_proc_handler(), "replaykit_clip_start", cd)
	local handled = called and obslua.calldata_bool(cd, "handled")
	obslua.calldata_destroy(cd)
	if not handled then
		obslua.obs_frontend_replay_buffer_start()
	end
end
