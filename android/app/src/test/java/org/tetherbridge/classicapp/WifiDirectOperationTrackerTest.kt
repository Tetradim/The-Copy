package org.tetherbridge.classicapp

import org.junit.Assert.*
import org.junit.Test

class WifiDirectOperationTrackerTest {
    @Test fun timedOutMutationBlocksOtherWritesUntilAndroidReplies() {
        val operations = WifiDirectOperationTracker()
        val first = operations.begin(changesGroup = true)
        operations.submitMutation(first)
        assertTrue(operations.finish(first))
        assertEquals(0L, operations.begin(changesGroup = true))

        val refresh = operations.begin(changesGroup = false)
        assertNotEquals(0L, refresh)
        assertTrue(operations.settleMutation(first))
        assertFalse(operations.isCurrent(first))
        assertTrue(operations.isCurrent(refresh))
        assertTrue(operations.finish(refresh))

        val second = operations.begin(changesGroup = true)
        operations.submitMutation(second)
        assertFalse(operations.settleMutation(first))
        assertTrue(operations.finish(second))
        assertEquals(0L, operations.begin(changesGroup = true))
        assertTrue(operations.settleMutation(second))
    }
}
