<?php
/**
 * Plugin Name: BTCPay Stablecoins gateway titles
 * Description: Gives the BTCPay "pay from other chains" gateways readable default titles ("Base Network" instead of LIFI_BASE).
 * Version: 1.0.0
 *
 * Install as a must-use plugin: copy this file to wp-content/mu-plugins/.
 *
 * The BTCPay for WooCommerce plugin (separate gateways mode) names each gateway after its BTCPay payment method id,
 * because BTCPay's API has no display name for it. This sets a readable title on each of these gateways once, only
 * while its title is still that id, so a title you change in WooCommerce > Settings > Payments is never overwritten.
 */

defined( 'ABSPATH' ) || exit;

function btcpay_stablecoins_gateway_titles(): array {
	return [
		// Tokens from a connected wallet, and USDC/USDT from an exchange.
		'LIFI_ETHEREUM'  => 'Ethereum Network',
		'LIFI_BASE'      => 'Base Network',
		'LIFI_ARBITRUM'  => 'Arbitrum Network',
		'LIFI_OPTIMISM'  => 'Optimism Network',
		'LIFI_POLYGON'   => 'Polygon Network',
		'LIFI_BSC'       => 'BNB Smart Chain',
		'LIFI_AVALANCHE' => 'Avalanche Network',
		'LIFI_SOLANA'    => 'Solana',
		'LIFI_TRON'      => 'Tron',
		// Coins, paid like a Litecoin invoice.
		'THOR_LITECOIN'  => 'Litecoin',
		'THOR_DOGECOIN'  => 'Dogecoin',
		'THOR_ZCASH'     => 'Zcash',
		'THOR_COSMOS'    => 'Cosmos (ATOM)',
		'THOR_XRP'       => 'XRP',
		'THOR_ETH'       => 'Ether (ETH)',
		'THOR_BASEETH'   => 'ETH on Base',
		'THOR_BNB'       => 'BNB',
		'THOR_AVAX'      => 'Avalanche (AVAX)',
		'THOR_SOL'       => 'Solana (SOL)',
		'THOR_TRX'       => 'Tron (TRX)',
	];
}

/** Saves the readable title into each gateway's settings, so the classic and the block checkout both show it. */
function btcpay_stablecoins_apply_gateway_titles(): void {
	foreach ( btcpay_stablecoins_gateway_titles() as $symbol => $title ) {
		$option   = 'woocommerce_btcpaygf_' . strtolower( $symbol ) . '_settings';
		$settings = get_option( $option );
		if ( ! is_array( $settings ) ) {
			// Never saved yet: WooCommerce fills in the other settings' defaults.
			$settings = [];
		}
		$current = isset( $settings['title'] ) ? trim( (string) $settings['title'] ) : '';
		if ( '' !== $current && 0 !== strcasecmp( $current, $symbol ) ) {
			continue;
		}
		$settings['title'] = $title;
		update_option( $option, $settings );
	}
}
add_action( 'admin_init', 'btcpay_stablecoins_apply_gateway_titles' );
add_action( 'woocommerce_init', 'btcpay_stablecoins_apply_gateway_titles' );

/** Before the settings are saved once (new gateways), show the readable title in the classic checkout too. */
add_filter( 'woocommerce_gateway_title', function ( $title, $id = '' ) {
	if ( 0 !== strpos( (string) $id, 'btcpaygf_' ) ) {
		return $title;
	}
	foreach ( btcpay_stablecoins_gateway_titles() as $symbol => $readable ) {
		if ( $id === 'btcpaygf_' . strtolower( $symbol ) && 0 === strcasecmp( trim( (string) $title ), $symbol ) ) {
			return $readable;
		}
	}
	return $title;
}, 10, 2 );
