package org.tetherbridge.classicapp

import org.junit.Assert.*
import org.junit.Test

class WifiDirectGroupPolicyTest {
    @Test fun refusesToReplaceExistingGroup() {
        assertFalse(WifiDirectGroupPolicy.mayCreate("DIRECT-ab-existing"))
        assertTrue(WifiDirectGroupPolicy.mayCreate(null))
    }
    @Test fun cannotCreateWhileAnEarlierCreateIsUnresolved() {
        assertFalse(WifiDirectGroupPolicy.mayCreateWhenPending(true))
        assertTrue(WifiDirectGroupPolicy.mayCreateWhenPending(false))
    }
    @Test fun rejectsAGroupWithSameNameButDifferentCredentials() {
        val digest = WifiDirectGroupPolicy.credentialDigest("DIRECT-ab-TetherBridge", "correct-password")
        assertTrue(WifiDirectGroupPolicy.credentialsMatch(digest, "DIRECT-ab-TetherBridge", "correct-password"))
        assertFalse(WifiDirectGroupPolicy.credentialsMatch(digest, "DIRECT-ab-TetherBridge", "wrong-password"))
        assertFalse(WifiDirectGroupPolicy.credentialsMatch(null, "DIRECT-ab-TetherBridge", "correct-password"))
    }
    @Test fun removesOnlyTheGroupCreatedByThisApp() {
        assertTrue(WifiDirectGroupPolicy.mayRemove("DIRECT-ab-TetherBridge", "DIRECT-ab-TetherBridge"))
        assertFalse(WifiDirectGroupPolicy.mayRemove("DIRECT-ab-TetherBridge", "DIRECT-cd-other"))
        assertFalse(WifiDirectGroupPolicy.mayRemove(null, "DIRECT-cd-other"))
        assertFalse(WifiDirectGroupPolicy.mayRemove("DIRECT-ab-TetherBridge", null))
    }
}
