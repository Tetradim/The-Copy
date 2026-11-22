package org.tetherbridge.classicapp

object WifiDirectGroupPolicy {
    fun credentialDigest(name: String, passphrase: String): String =
        java.security.MessageDigest.getInstance("SHA-256").digest((name + "\u0000" + passphrase).toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }

    fun credentialsMatch(expected: String?, name: String?, passphrase: String?): Boolean {
        if (expected == null || name == null || passphrase == null) return false
        return java.security.MessageDigest.isEqual(expected.toByteArray(Charsets.US_ASCII), credentialDigest(name, passphrase).toByteArray(Charsets.US_ASCII))
    }

    fun mayCreateWhenPending(pending: Boolean): Boolean = !pending
    fun mayCreate(existingName: String?): Boolean = existingName == null
    fun mayRemove(ownedName: String?, currentName: String?): Boolean =
        !ownedName.isNullOrEmpty() && ownedName == currentName
}
