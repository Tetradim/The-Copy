package org.tetherbridge.classicapp

// A UI timeout cannot cancel an operation already submitted to Android.
internal class WifiDirectOperationTracker {
    private var generation = 0L
    private var active = 0L
    private var mutation = 0L

    @Synchronized fun begin(changesGroup: Boolean): Long {
        if (active != 0L || (changesGroup && mutation != 0L)) return 0
        active = ++generation
        return active
    }

    @Synchronized fun isCurrent(id: Long): Boolean = id != 0L && active == id

    @Synchronized fun finish(id: Long): Boolean {
        if (!isCurrent(id)) return false
        active = 0
        return true
    }

    @Synchronized fun submitMutation(id: Long) {
        check(isCurrent(id) && mutation == 0L)
        mutation = id
    }

    @Synchronized fun settleMutation(id: Long): Boolean {
        if (id == 0L || mutation != id) return false
        mutation = 0
        return true
    }
}
